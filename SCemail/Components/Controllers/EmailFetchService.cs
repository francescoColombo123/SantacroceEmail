using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MimeKit;
using Oracle.ManagedDataAccess.Client;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Threading;
using System.Threading.Tasks;

public class EmailFetchService : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly ILogger<EmailFetchService> _logger;
    public bool IsNightNow() => IsNightWindow();

    // Quante mail max per cartella ad ogni passata (fairness)
    private const int MaxPerFolderPerRun = 50;
    private readonly string _attachmentsBasePath;

    // Ogni quanto ripassare
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(1);
    private static readonly TimeZoneInfo RomeTz =
    TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "W. Europe Standard Time" : "Europe/Rome"
    );
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> _ensureLocks = new();


    public EmailFetchService(IConfiguration config, ILogger<EmailFetchService> logger)
    {
        _config = config;
        _logger = logger;
        _attachmentsBasePath = _config.GetValue<string>("Attachments:BasePath")
       ?? Path.Combine(AppContext.BaseDirectory, "attachments");

        _attachmentsBasePath = _attachmentsBasePath.Trim();
        Directory.CreateDirectory(_attachmentsBasePath);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = _config.GetValue<bool?>("EmailFetch:Enabled") ?? true;

        _logger.LogInformation("EmailFetchService avviato - intervallo {Minuti} minuti", _interval.TotalMinutes);
        if (!enabled)
        {
            _logger.LogWarning("EmailFetchService DISABILITATO via config (EmailFetch:Enabled=false).");
            return;
        }
        _logger.LogInformation("EmailFetchService avviato - intervallo {Minuti} minuti", _interval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            var night = IsNightWindow();
            _logger.LogInformation("🕒 Finestra attuale: {Mode}", night ? "NOTTE (backfill ON)" : "GIORNO (solo nuove)");

            try
            {
                await ProcessAllMailboxes(stoppingToken, night);

                // ✅ backfill allegati SOLO di notte
                if (night)
                    await BackfillMissingAttachmentsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Errore generale nel fetch email");
            }

            try { await Task.Delay(_interval, stoppingToken); } catch { /* ignore */ }
        }
    }

    private static bool IsNightWindow()
    {
        var nowRome = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, RomeTz).TimeOfDay;
        // notte: 23:00 -> 06:00
        return nowRome >= TimeSpan.FromHours(23) || nowRome < TimeSpan.FromHours(6);
    }

    public async Task ProcessAllMailboxes(CancellationToken ct, bool nightMode)
    {
        var connString = _config.GetConnectionString("OracleDb")
                         ?? throw new InvalidOperationException("ConnectionString 'OracleDb' mancante.");

        // 1) Carico TUTTE le caselle in memoria e chiudo il reader/connessione
        var accounts = new List<(int Id, string Email, string Password, string Host, int Port, bool UseSsl)>();

        await using (var conn = new OracleConnection(connString))
        {
            await conn.OpenAsync(ct);

            const string sql = @"SELECT ID, EMAIL, PASSWORD, IMAP_HOST, IMAP_PORT, USE_SSL FROM SGAPP.CASELLEPOSTA";
            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetInt32(0);
                var email = reader.GetString(1);
                var pwd = reader.GetString(2);
                var host = reader.GetString(3);
                var port = reader.GetInt32(4);
                var useSsl = string.Equals(reader.GetString(5), "Y", StringComparison.OrdinalIgnoreCase);
                accounts.Add((id, email, pwd, host, port, useSsl));
            }
        }

        _logger.LogInformation("Totale caselle da processare: {N}", accounts.Count);

        // 2) Round-robin: per OGNI casella apro una connessione DB separata e process
        foreach (var a in accounts)
        {
            try
            {
                _logger.LogInformation("=== INIZIO CASELLA {Email} ({Host}:{Port}, SSL={SSL}) ===",
                    a.Email, a.Host, a.Port, a.UseSsl);

                await using var accountConn = new OracleConnection(connString);
                await accountConn.OpenAsync(ct);

                await FetchEmailsForAccount(a.Id, a.Email, a.Password, a.Host, a.Port, a.UseSsl, accountConn, ct, nightMode);

                _logger.LogInformation("=== FINE CASELLA {Email} ===", a.Email);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Errore nella casella {Email}", a.Email);
            }
        }
    }

    private async Task FetchEmailsForAccount(
     int casellaId, string email, string password, string host, int port, bool useSsl,
     OracleConnection dbConn, CancellationToken ct, bool nightMode)
    {
        if (casellaId <= 0) return;

        // Log di protocollo IMAP su file (nella cartella dell’eseguibile)
        using var proto = new ProtocolLogger(Stream.Null);   // <-- NON scrive su disco
        using var client = new ImapClient(proto);

        // Se vuoi vedere eventuali problemi TLS/certificato
        client.ServerCertificateValidationCallback = (s, cert, chain, errors) =>
        {
            if (errors == System.Net.Security.SslPolicyErrors.None) return true;
            _logger.LogError("SSL error per {Email}: {Errors}", email, errors);
            return false; // non accettare certificati non validi
        };

        client.AuthenticationMechanisms.Remove("XOAUTH2");
        var socket = useSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;

        try
        {
            await client.ConnectAsync(host, port, socket, ct);
            _logger.LogInformation("Connesso a {Email}", email);

            await client.AuthenticateAsync(email, password, ct);
            _logger.LogInformation("Autenticato su {Email}", email);

            // ✅ da qui in poi il tuo codice “solo INBOX” (già deduplicato)
            var status = StatusItems.Count | StatusItems.Recent | StatusItems.Unread;

            // 1) raccolgo inbox + tutte le sottocartelle
            var foldersToProcess = new List<IMailFolder>();

            foldersToProcess.Add(client.Inbox);

            TryAddSpecial(client, SpecialFolder.All, foldersToProcess, email);
            TryAddSpecial(client, SpecialFolder.Junk, foldersToProcess, email);

            // dedup + noselect
            foldersToProcess = foldersToProcess
                .Where(f => f != null)
                .Where(f => !string.IsNullOrWhiteSpace(f.FullName))
                .Where(f => (f.Attributes & FolderAttributes.NoSelect) == 0)
                .GroupBy(f => NormalizeFolderPath(f.FullName!))
                .Select(g => g.First())
                .ToList();

            _logger.LogInformation("Cartelle selezionate per {Email}: {Folders}",
                email, string.Join(" | ", foldersToProcess.Select(f => f.FullName)));
          
            foreach (var folder in foldersToProcess)
            {
                await folder.StatusAsync(status, ct);
                await folder.OpenAsync(FolderAccess.ReadOnly, ct);

                _logger.LogInformation("📂 Leggo cartella '{Folder}' (Unread={Unread} Recent={Recent} Count={Count})",
                    folder.FullName, folder.Unread, folder.Recent, folder.Count);

                List<IMessageSummary> newSummaries;

                if (!nightMode)
                {
                    // GIORNO: solo oggi (anche All/Spam)
                    newSummaries = await FetchTodayBatchAsync(dbConn, folder, casellaId, folder.FullName, ct);
                    _logger.LogInformation("DAYSCAN cid={Cid} folder='{Folder}' oggi={N}", casellaId, folder.FullName, newSummaries.Count);
                }
                else
                {
                    // NOTTE: nuove a UID
                    newSummaries = await FetchNewBatchAsync(dbConn, folder, casellaId, folder.FullName, ct, nightMode);
                    if (newSummaries.Count > 0)
                        _logger.LogInformation("🆕 '{Folder}': nuove da processare={N}", folder.FullName, newSummaries.Count);
                }

                await ProcessSummariesAsync(folder, newSummaries, dbConn, casellaId, email, ct);

                // ✅ B) BACKFILL SOLO DI NOTTE (UNA VOLTA)
                if (nightMode)
                {
                    var backSummaries = await FetchBackfillBatchAsync(dbConn, folder, casellaId, folder.FullName, ct);
                    if (backSummaries.Count > 0)
                        _logger.LogInformation("⏪ '{Folder}': backfill da processare={N}", folder.FullName, backSummaries.Count);

                    await ProcessSummariesAsync(folder, backSummaries, dbConn, casellaId, email, ct);
                }

                await folder.CloseAsync(false, ct);
            }

        }

        catch (AuthenticationException ex) // MailKit.Security.AuthenticationException
        {
            _logger.LogError(ex, "Autenticazione IMAP fallita per {Email}. Verifica APP PASSWORD (Gmail).", email);
        }
        catch (ServiceNotAuthenticatedException ex)
        {
            _logger.LogError(ex, "Non autenticato su {Email} (IMAP).", email);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogWarning("Operazione cancellata per {Email}.", email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Errore non gestito durante FetchEmails per {Email}.", email);
        }
        finally
        {
            try { if (client.IsConnected) await client.DisconnectAsync(true, ct); }
            catch { /* ignora */ }
            _logger.LogInformation("Disconnesso da {Email}", email);
        }
    }

    private void TryAddSpecial(ImapClient client, SpecialFolder special, List<IMailFolder> list, string email)
    {
        try
        {
            var f = client.GetFolder(special);
            if (f != null) { list.Add(f); return; }
        }
        catch { }

        // fallback PEC Aruba
        try
        {
            if (special == SpecialFolder.Junk)
            {
                var f = client.GetFolder("INBOX.Spam");
                if (f != null) list.Add(f);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SpecialFolder {Spec} non risolto per {Email}", special, email);
        }
    }


    private async Task ProcessSummariesAsync(
    IMailFolder folder,
    List<IMessageSummary> summaries,
    OracleConnection dbConn,
    int casellaId,
    string accountEmail,
    CancellationToken ct)
    {
        if (summaries == null || summaries.Count == 0) return;

        foreach (var s in summaries)
        {
            var full = await folder.GetMessageAsync(s.UniqueId, ct);

            var mid = full.MessageId?.Trim();
            if (string.IsNullOrWhiteSpace(mid))
                mid = BuildStableFallbackMessageId(full);

            var existingId = await GetExistingEmailIdByMessageId(dbConn, casellaId, mid, ct);

            int emailId;
            if (existingId.HasValue)
            {
                emailId = existingId.Value;
                await TouchExistingEmail(dbConn, emailId, (long)s.UniqueId.Id, ct);
            }
            else
            {
                var uid = (long)s.UniqueId.Id;
                var emailUtc = s.InternalDate?.UtcDateTime ?? full.Date.UtcDateTime;

                emailId = await SaveEmail(dbConn, casellaId, mid, full, ct, folder.FullName, uid, emailUtc);
            }

            var emailUtc2 = s.InternalDate?.UtcDateTime ?? full.Date.UtcDateTime;
            await ApplyRulesAsync(dbConn, emailId, full, emailUtc2, ct);

            var atts = await SaveAttachmentsMetadata(dbConn, emailId, s.Body, ct);
            await DownloadAndStoreAttachmentsAsync(folder, s.UniqueId, casellaId, emailId, atts, s.Body, dbConn, ct);

            if (existingId.HasValue)
            {
                _logger.LogDebug(
                    "↪︎ GIÀ PRESENTE id={Id} [{Acc}] {Folder} uid={Uid}",
                    emailId, accountEmail, folder.FullName, (long)s.UniqueId.Id
                );
            }
            else
            {
                _logger.LogInformation(
                    "🆕 NUOVA id={Id} [{Acc}] {Folder} uid={Uid} subj='{Subj}'",
                    emailId, accountEmail, folder.FullName, (long)s.UniqueId.Id, full.Subject
                );
            }
        }
    }

    private async Task<long> GetLastSavedUid(OracleConnection conn, int casellaId, string folderPath, CancellationToken ct)
    {
        const string sql = @"
            SELECT NVL(MAX(MESSAGE_UID), 0)
              FROM SGAPP.EMAIL_RICEVUTE
             WHERE CASELLA_ID = :p_cid
               AND FOLDER_PATH = :p_fp";

        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
        cmd.Parameters.Add("p_cid", OracleDbType.Int32).Value = casellaId;
        cmd.Parameters.Add("p_fp", OracleDbType.Varchar2, 512).Value =
            string.IsNullOrEmpty(folderPath) ? "" :
            (folderPath.Length <= 512 ? folderPath : folderPath[..512]);

        var obj = await cmd.ExecuteScalarAsync(ct);
        if (obj == null || obj == DBNull.Value) return 0L;
        return Convert.ToInt64(obj);
    }

    private async Task<bool> EmailExists(OracleConnection conn, int casellaId, string folderPath, long uid, CancellationToken ct)
    {
        const string sql = @"
            SELECT COUNT(1)
              FROM SGAPP.EMAIL_RICEVUTE
             WHERE CASELLA_ID = :p_cid
               AND FOLDER_PATH = :p_fp
               AND MESSAGE_UID = :p_uid";

        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
        cmd.Parameters.Add("p_cid", OracleDbType.Int32).Value = casellaId;
        cmd.Parameters.Add("p_fp", OracleDbType.Varchar2, 512).Value =
            string.IsNullOrEmpty(folderPath) ? "" :
            (folderPath.Length <= 512 ? folderPath : folderPath[..512]);
        cmd.Parameters.Add("p_uid", OracleDbType.Int64).Value = uid;

        var countObj = await cmd.ExecuteScalarAsync(ct);
        var count = Convert.ToInt32(countObj);
        return count > 0;
    }

    private async Task<int> SaveEmail(
     OracleConnection conn,
     int casellaId,
     string messageId,
     MimeMessage message,
     CancellationToken ct,
     string folderPath,
     long? messageUid,
     DateTime? internalDateUtc = null)
    {
        string? threadKey = null;

        // 1️⃣ Se ha In-Reply-To, cerca quel messaggio nel DB
        if (!string.IsNullOrEmpty(message.InReplyTo))
        {
            const string sqlFind = @"
        SELECT THREAD_KEY 
        FROM SGAPP.EMAIL_RICEVUTE 
        WHERE MESSAGE_ID = :p_mid
        UNION ALL
        SELECT THREAD_KEY 
        FROM SGAPP.EMAIL_INVIATE 
        WHERE MESSAGE_ID = :p_mid";

            await using var cmdFind = new OracleCommand(sqlFind, conn) { BindByName = true };
            cmdFind.Parameters.Add("p_mid", OracleDbType.Varchar2, 500).Value = message.InReplyTo;
            var obj = await cmdFind.ExecuteScalarAsync(ct);
            if (obj != null && obj != DBNull.Value)
                threadKey = obj.ToString();
        }

        // 2️⃣ Se non trovato ma ha References, prova l’ultimo ID
        if (string.IsNullOrEmpty(threadKey) && message.References != null && message.References.Any())
        {
            var lastRef = message.References.Last();
            const string sqlFindRef = @"
        SELECT THREAD_KEY 
        FROM SGAPP.EMAIL_RICEVUTE 
        WHERE MESSAGE_ID = :p_mid
        UNION ALL
        SELECT THREAD_KEY 
        FROM SGAPP.EMAIL_INVIATE 
        WHERE MESSAGE_ID = :p_mid";
            await using var cmdFindRef = new OracleCommand(sqlFindRef, conn) { BindByName = true };
            cmdFindRef.Parameters.Add("p_mid", OracleDbType.Varchar2, 500).Value = lastRef;
            var obj2 = await cmdFindRef.ExecuteScalarAsync(ct);
            if (obj2 != null && obj2 != DBNull.Value)
                threadKey = obj2.ToString();
        }

        // 3️⃣ Se ancora nulla, crea un nuovo thread con MessageId proprio
        if (string.IsNullOrEmpty(threadKey))
            threadKey = messageId;

        // ✅ Inserimento email
        const string sql = @"
        INSERT INTO SGAPP.EMAIL_RICEVUTE
            (CASELLA_ID, MESSAGE_ID, DATA_RICEZIONE, MITTENTE, DESTINATARI,CC, CCN, OGGETTO,
             CORPO_HTML, CORPO_TESTO, APERTO, ELIMINATO, FOLDER_PATH, MESSAGE_UID,
             IN_REPLY_TO, REFERENCES_HDR, THREAD_KEY)
        VALUES
            (:p_cid, :p_mid, :p_dt, :p_from, :p_to,:p_cc, :p_ccn, :p_subj,
             :p_html, :p_text, 'N', 'N', :p_fp, :p_uid,
             :p_inreply, :p_refs, :p_thread)
        RETURNING ID INTO :p_id";

        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };

        cmd.Parameters.Add("p_cid", OracleDbType.Int32).Value = casellaId;
        cmd.Parameters.Add("p_mid", OracleDbType.Varchar2, 500).Value = messageId;
        cmd.Parameters.Add("p_dt", OracleDbType.Date).Value = (internalDateUtc ?? message.Date.UtcDateTime);
        cmd.Parameters.Add("p_from", OracleDbType.Varchar2, 500).Value = message.From?.ToString() ?? "";
        cmd.Parameters.Add("p_to", OracleDbType.Varchar2, 2000).Value = message.To?.ToString() ?? "";
        cmd.Parameters.Add("p_cc", OracleDbType.Varchar2, 2000).Value = message.Cc?.ToString() ?? "";
        cmd.Parameters.Add("p_ccn", OracleDbType.Varchar2, 2000).Value = message.Bcc?.ToString() ?? "";
        var emailUtc = internalDateUtc ?? message.Date.UtcDateTime;

        string subject = message.Subject?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(subject))
        {
            var from = message.From?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(from)) from = "Sconosciuto";

            subject = $"(Senza oggetto) da {from} - {emailUtc:yyyy-MM-dd HH:mm}";
        }
        var fp = NormalizeFolderPath(folderPath);
        if (subject.Length > 1000) subject = subject[..1000];
        cmd.Parameters.Add("p_subj", OracleDbType.Varchar2, 1000).Value = subject;
        cmd.Parameters.Add("p_html", OracleDbType.Clob).Value = (object?)message.HtmlBody ?? DBNull.Value;
        cmd.Parameters.Add("p_text", OracleDbType.Clob).Value = (object?)message.TextBody ?? DBNull.Value;
        cmd.Parameters.Add("p_fp", OracleDbType.Varchar2, 512).Value = fp;

        cmd.Parameters.Add("p_uid", OracleDbType.Int64).Value = (object?)messageUid ?? DBNull.Value;
        cmd.Parameters.Add("p_inreply", OracleDbType.Varchar2, 500)
            .Value = message.InReplyTo ?? (object)DBNull.Value;
        cmd.Parameters.Add("p_refs", OracleDbType.Clob)
            .Value = message.References != null && message.References.Any()
                ? string.Join(" ", message.References)
                : (object)DBNull.Value;
        cmd.Parameters.Add("p_thread", OracleDbType.Varchar2, 500)
            .Value = threadKey ?? (object)DBNull.Value;

        var outId = new OracleParameter("p_id", OracleDbType.Int32) { Direction = ParameterDirection.Output };
        cmd.Parameters.Add(outId);

        try
        {
            await cmd.ExecuteNonQueryAsync(ct);

            if (outId.Value is Oracle.ManagedDataAccess.Types.OracleDecimal o)
                return o.ToInt32();

            return Convert.ToInt32(outId.Value?.ToString());
        }
        catch (OracleException ex) when (ex.Number == 1) // ORA-00001 unique constraint
        {
            // esiste già: ritorno l'ID esistente
            var existingId = await GetExistingEmailIdByMessageId(conn, casellaId, messageId, ct);
            if (existingId.HasValue)
                return existingId.Value;

            // se per qualche motivo non lo trova, rilancio
            throw;
        }

    }
    private async Task TouchExistingEmail(OracleConnection conn, int emailId, long uid, CancellationToken ct)
    {
        const string sql = @"
                            UPDATE SGAPP.EMAIL_RICEVUTE
                               SET LAST_EVENT_AT = SYSTIMESTAMP,
                                   MESSAGE_UID   = NVL(MESSAGE_UID, :p_uid)
                             WHERE ID = :p_id";

        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
        cmd.Parameters.Add("p_uid", OracleDbType.Int64).Value = uid;
        cmd.Parameters.Add("p_id", OracleDbType.Int32).Value = emailId;
        await cmd.ExecuteNonQueryAsync(ct);
    }


    private static string BuildStableFallbackMessageId(MimeMessage m)
    {
        // qualcosa di stabile:
        var from = m.From?.ToString() ?? "";
        var to = m.To?.ToString() ?? "";
        var subj = m.Subject ?? "";
        var date = m.Date.UtcDateTime.ToString("O");

        // non usare corpo (pesante), basta header “stabili”
        var key = $"{from}|{to}|{subj}|{date}".Trim();

        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(key);
        var hash = Convert.ToHexString(sha.ComputeHash(bytes));
        return $"fallback:{hash}";
    }

    private async Task<List<(int AllegatoId, string FileName, string Mime, string PartSpec)>> SaveAttachmentsMetadata(
     OracleConnection conn, int emailId, BodyPart? body, CancellationToken ct)
    {
        var res = new List<(int, string, string, string)>();
        if (body is null) return res;

        var list = new List<(string FileName, string Mime, string PartSpec)>();
        CollectAttachmentParts(body, list);

        foreach (var a in list)
        {
            const string existsSql = @"
SELECT ID, NOME_FILE, MIME_TYPE, PART_SPEC
  FROM SGAPP.EMAIL_ALLEGATI
 WHERE EMAIL_ID = :p_eid
   AND PART_SPEC = :p_part";

            await using (var cmdEx = new OracleCommand(existsSql, conn) { BindByName = true })
            {
                cmdEx.Parameters.Add("p_eid", OracleDbType.Int32).Value = emailId;
                cmdEx.Parameters.Add("p_part", OracleDbType.Varchar2, 64).Value = a.PartSpec ?? "";

                await using var r = await cmdEx.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    var existingId = r.GetInt32(0);
                    var fn = r.IsDBNull(1) ? (a.FileName ?? "allegato") : r.GetString(1);
                    var mm = r.IsDBNull(2) ? (a.Mime ?? "application/octet-stream") : r.GetString(2);
                    var ps = r.IsDBNull(3) ? (a.PartSpec ?? "") : r.GetString(3);

                    res.Add((existingId, fn, mm, ps));
                    continue;
                }
            }
            const string sql = @"
            INSERT INTO SGAPP.EMAIL_ALLEGATI (EMAIL_ID, NOME_FILE, MIME_TYPE, PART_SPEC)
            VALUES (:p_eid, :p_name, :p_mime, :p_part)
            RETURNING ID INTO :p_id";

            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
            cmd.Parameters.Add("p_eid", OracleDbType.Int32).Value = emailId;
            cmd.Parameters.Add("p_name", OracleDbType.Varchar2, 512).Value = a.FileName ?? "allegato";
            cmd.Parameters.Add("p_mime", OracleDbType.Varchar2, 255).Value = a.Mime ?? "application/octet-stream";
            cmd.Parameters.Add("p_part", OracleDbType.Varchar2, 64).Value = a.PartSpec ?? "";

            var outId = new OracleParameter("p_id", OracleDbType.Int32) { Direction = ParameterDirection.Output };
            cmd.Parameters.Add(outId);

            await cmd.ExecuteNonQueryAsync(ct);

            var id = (outId.Value is Oracle.ManagedDataAccess.Types.OracleDecimal od) ? od.ToInt32() : Convert.ToInt32(outId.Value);
            res.Add((id, a.FileName ?? "allegato", a.Mime ?? "application/octet-stream", a.PartSpec ?? ""));
        }

        return res;
    }


    private static void CollectAttachmentParts(BodyPart part, List<(string FileName, string Mime, string PartSpec)> acc)
    {
        if (part is BodyPartBasic basic)
        {
            var fileName = basic.FileName;
            var disp = basic.ContentDisposition?.Disposition;

             var isAttachment =
                string.Equals(disp, "attachment", StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(fileName) && !string.Equals(disp, "inline", StringComparison.OrdinalIgnoreCase));


            if (isAttachment)
            {
                acc.Add((
                    string.IsNullOrWhiteSpace(fileName) ? "allegato" : fileName,
                    basic.ContentType?.MimeType ?? "application/octet-stream",
                    basic.PartSpecifier
                ));
            }
        }

        if (part is BodyPartMultipart mp)
        {
            foreach (var child in mp.BodyParts)
                CollectAttachmentParts(child, acc);
        }
    }
    private async Task ApplyRulesAsync(OracleConnection conn, int emailId, MimeMessage message, DateTime emailDateUtc, CancellationToken ct)
    {
        const string sql = @"
        SELECT ID, MITTENTE_LIKE, DEST_LIKE, OGGETTO_LIKE, ASSEGNA_A, SOLO_INVIO
          FROM SGAPP.EMAIL_REGOLE
         WHERE ATTIVA = 'Y'
        AND CREATED_AT <= :p_mail_dt";

        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
        cmd.Parameters.Add("p_mail_dt", OracleDbType.Date).Value = emailDateUtc;

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetInt32(0);
            var mittLike = reader.IsDBNull(1) ? null : reader.GetString(1);
            var destLike = reader.IsDBNull(2) ? null : reader.GetString(2);
            var oggLike = reader.IsDBNull(3) ? null : reader.GetString(3);
            var utenti = reader.IsDBNull(4) ? null : reader.GetString(4);
            var soloInvio = reader.GetString(5) == "Y";

            if (CheckRuleMatch(message, mittLike, destLike, oggLike))
            {
                await AssignEmail(conn, emailId, utenti, soloInvio, ct);
                _logger.LogInformation("📥 Applicata regola {Id} per email '{Subj}'", id, message.Subject);
            }
        }
    }

    private async Task DownloadAndStoreAttachmentsAsync(
     IMailFolder folder,
     UniqueId uid,
     int casellaId,
     int emailId,
     List<(int AllegatoId, string FileName, string Mime, string PartSpec)> attachments,
     BodyPart? bodyStructure,
     OracleConnection conn,
     CancellationToken ct)
    {
        if (attachments == null || attachments.Count == 0) return;

        // se bodyStructure è null, lo ricarico al volo (fallback)
        if (bodyStructure == null)
        {
            var sums = await folder.FetchAsync(new[] { uid }, MessageSummaryItems.BodyStructure, ct);
            bodyStructure = sums.FirstOrDefault()?.Body;
            if (bodyStructure == null)
            {
                _logger.LogWarning("BodyStructure NULL: salto allegati per emailId={EmailId} uid={Uid}", emailId, uid);
                return;
            }
        }

        var emailDir = Path.Combine(_attachmentsBasePath, casellaId.ToString(), emailId.ToString());
        Directory.CreateDirectory(emailDir);

        foreach (var a in attachments)
        {
            string? filePath = null;

            try
            {
                var safeName = SanitizeFileName(string.IsNullOrWhiteSpace(a.FileName) ? "allegato" : a.FileName);
                filePath = Path.Combine(emailDir, $"{a.AllegatoId}_{safeName}");
                var relPath = Path.Combine(casellaId.ToString(), emailId.ToString(), $"{a.AllegatoId}_{safeName}");

                var bp = FindBodyPartBySpecifier(bodyStructure, a.PartSpec);
                if (bp == null)
                {
                    _logger.LogWarning("BodyPart non trovato: emailId={EmailId} allegatoId={AllegatoId} partSpec='{PartSpec}'",
                        emailId, a.AllegatoId, a.PartSpec ?? "(null)");
                    continue;
                }

                var entity = await folder.GetBodyPartAsync(uid, bp, ct);
                if (entity == null)
                {
                    _logger.LogWarning("GetBodyPartAsync ha restituito NULL: emailId={EmailId} allegatoId={AllegatoId} partSpec='{PartSpec}'",
                        emailId, a.AllegatoId, a.PartSpec ?? "(null)");
                    continue;
                }

                long size;
                await using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    if (entity is MimePart mp) await mp.Content.DecodeToAsync(fs, ct);
                    else if (entity is MessagePart msgPart) await msgPart.Message.WriteToAsync(fs, ct);
                    else await entity.WriteToAsync(fs, ct);

                    await fs.FlushAsync(ct);
                    size = fs.Length;
                }

                const string upd = @"
UPDATE SGAPP.EMAIL_ALLEGATI
   SET FILE_PATH = :p_path,
       FILE_SIZE = :p_size
 WHERE ID = :p_id
   AND FILE_PATH IS NULL";

                await using var cmd = new OracleCommand(upd, conn) { BindByName = true };
                cmd.Parameters.Add("p_path", OracleDbType.Varchar2, 1024).Value = relPath;
                cmd.Parameters.Add("p_size", OracleDbType.Int64).Value = size;
                cmd.Parameters.Add("p_id", OracleDbType.Int32).Value = a.AllegatoId;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (Exception ex)
            {
                try { if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath)) File.Delete(filePath); } catch { }
                _logger.LogError(ex, "Errore salvataggio allegato ID={AllegatoId} email={EmailId}", a.AllegatoId, emailId);
            }
        }
    }


    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');

        // evita path traversal e nomi strani
        name = name.Replace("..", "_").Trim();
        if (name.Length > 200) name = name[..200];
        return name;
    }

    private bool CheckRuleMatch(MimeMessage msg, string? mitt, string? dest, string? subj)
    {
        var allRecipients =
            (msg.To?.ToString() ?? "") + " " +
            (msg.Cc?.ToString() ?? "") + " " +
            (msg.Bcc?.ToString() ?? "");
        bool MittOk =
            string.IsNullOrEmpty(mitt) ||
            mitt.Equals("TUTTI", StringComparison.OrdinalIgnoreCase) ||
            msg.From.ToString().Contains(mitt, StringComparison.OrdinalIgnoreCase);

        bool DestOk =
          string.IsNullOrEmpty(dest) ||
          dest.Equals("TUTTI", StringComparison.OrdinalIgnoreCase) ||
          allRecipients.Contains(dest, StringComparison.OrdinalIgnoreCase);

        bool SubjOk =
            string.IsNullOrEmpty(subj) ||
            subj.Equals("TUTTI", StringComparison.OrdinalIgnoreCase) ||
            (msg.Subject?.Contains(subj, StringComparison.OrdinalIgnoreCase) ?? false);

        return MittOk && DestOk && SubjOk;
    }
    private static BodyPart? FindBodyPartBySpecifier(BodyPart? part, string partSpec)
    {
        if (part == null) return null;

        // match diretto
        if (string.Equals(part.PartSpecifier, partSpec, StringComparison.Ordinal))
            return part;

        // ricorsione sui multipart
        if (part is BodyPartMultipart mp)
        {
            foreach (var child in mp.BodyParts)
            {
                var found = FindBodyPartBySpecifier(child, partSpec);
                if (found != null) return found;
            }
        }

        return null;
    }

    private async Task AssignEmail(OracleConnection conn, int emailId, string? utenti, bool soloInvio, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(utenti))
            return;

        var arr = utenti.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        const string sql = @"
MERGE INTO SGAPP.EMAIL_ASSEGNAZIONI t
USING (SELECT :p_eid AS EMAIL_ID, :p_user AS UTENTE, :p_solo AS SOLO_INVIO FROM dual) s
ON (t.EMAIL_ID = s.EMAIL_ID AND t.UTENTE = s.UTENTE AND t.SOLO_INVIO = s.SOLO_INVIO)
WHEN NOT MATCHED THEN
  INSERT (EMAIL_ID, UTENTE, SOLO_INVIO)
  VALUES (s.EMAIL_ID, s.UTENTE, s.SOLO_INVIO)";

        foreach (var u in arr)
        {
            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
            cmd.Parameters.Add("p_eid", OracleDbType.Int32).Value = emailId;
            cmd.Parameters.Add("p_user", OracleDbType.Varchar2, 200).Value = u;
            cmd.Parameters.Add("p_solo", OracleDbType.Char, 1).Value = soloInvio ? "Y" : "N";
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }


    // ======================================================================
    // 🔁 JOB RETROATTIVO: applica regole anche alle email già salvate
    // ======================================================================
    private async Task ApplyRulesToExistingEmailsAsync(OracleConnection conn, CancellationToken ct)
    {
        _logger.LogInformation("🔁 Avvio job di rielaborazione email archiviate...");

        const string regoleSql = @"
        SELECT ID, MITTENTE_LIKE, DEST_LIKE, OGGETTO_LIKE, ASSEGNA_A, SOLO_INVIO
          FROM SGAPP.EMAIL_REGOLE
         WHERE ATTIVA = 'Y'";
        var regole = new List<(int Id, string? MittLike, string? DestLike, string? OggLike, string? Utenti, bool SoloInvio)>();

        await using (var cmd = new OracleCommand(regoleSql, conn))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                regole.Add((
                    reader.GetInt32(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    !reader.IsDBNull(5) && reader.GetString(5) == "Y"
                ));
            }
        }

        if (regole.Count == 0)
        {
            _logger.LogInformation("⚠️ Nessuna regola attiva trovata.");
            return;
        }

        const string emailSql = @"
        SELECT ID, MITTENTE, DESTINATARI, OGGETTO
          FROM SGAPP.EMAIL_RICEVUTE e
         WHERE NOT EXISTS (SELECT 1 FROM SGAPP.EMAIL_ASSEGNAZIONI a WHERE a.EMAIL_ID = e.ID)";

        var emails = new List<(int Id, string Mitt, string Dest, string Ogg)>();
        await using (var cmd2 = new OracleCommand(emailSql, conn))
        await using (var reader2 = await cmd2.ExecuteReaderAsync(ct))
        {
            while (await reader2.ReadAsync(ct))
            {
                emails.Add((
                    reader2.GetInt32(0),
                    reader2.IsDBNull(1) ? "" : reader2.GetString(1),
                    reader2.IsDBNull(2) ? "" : reader2.GetString(2),
                    reader2.IsDBNull(3) ? "" : reader2.GetString(3)
                ));
            }
        }

        _logger.LogInformation("📬 Email non assegnate trovate: {N}", emails.Count);

        foreach (var e in emails)
        {
            // normalizzo mittente/destinatari togliendo spazi e simboli inutili
            string mittNorm = e.Mitt.Replace("<", "").Replace(">", "").Trim();

            string destNorm = e.Dest.Replace("<", "").Replace(">", "").Trim();
            string oggNorm = e.Ogg?.Trim() ?? "";

            foreach (var r in regole)
            {
                bool mittOk =
                    string.IsNullOrEmpty(r.MittLike) ||
                    r.MittLike.Equals("TUTTI", StringComparison.OrdinalIgnoreCase) ||
                    mittNorm.Contains(r.MittLike, StringComparison.OrdinalIgnoreCase);

                bool destOk =
                    string.IsNullOrEmpty(r.DestLike) ||
                    r.DestLike.Equals("TUTTI", StringComparison.OrdinalIgnoreCase) ||
                    destNorm.Contains(r.DestLike, StringComparison.OrdinalIgnoreCase);

                bool oggOk =
                    string.IsNullOrEmpty(r.OggLike) ||
                    r.OggLike.Equals("TUTTI", StringComparison.OrdinalIgnoreCase) ||
                    oggNorm.Contains(r.OggLike, StringComparison.OrdinalIgnoreCase);

                if (mittOk && destOk && oggOk)
                {
                    await AssignEmail(conn, e.Id, r.Utenti, r.SoloInvio, ct);
                    _logger.LogInformation("♻️ Applicata regola {R} su email ID={E} (oggetto='{O}')", r.Id, e.Id, e.Ogg);
                }
            }
        }
    }
    private async Task<List<(int AllegatoId, int EmailId, int CasellaId, string FolderPath, long MessageUid, string FileName, string Mime, string PartSpec)>>
        LoadMissingAttachmentsAsync(OracleConnection conn, int limit, CancellationToken ct)
            {
                const string sql = @"
        SELECT a.ID AS ALLEGATO_ID,
               a.EMAIL_ID,
               e.CASELLA_ID,
               e.FOLDER_PATH,
               e.MESSAGE_UID,
               a.NOME_FILE,
               a.MIME_TYPE,
               a.PART_SPEC
          FROM SGAPP.EMAIL_ALLEGATI a
          JOIN SGAPP.EMAIL_RICEVUTE e ON e.ID = a.EMAIL_ID
         WHERE a.FILE_PATH IS NULL
           AND e.MESSAGE_UID IS NOT NULL
         ORDER BY e.CASELLA_ID, e.FOLDER_PATH, e.MESSAGE_UID, a.ID
         FETCH FIRST :p_limit ROWS ONLY";

                var list = new List<(int, int, int, string, long, string, string, string)>();

                await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
                cmd.Parameters.Add("p_limit", OracleDbType.Int32).Value = limit;

                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    list.Add((
                        r.GetInt32(0),
                        r.GetInt32(1),
                        r.GetInt32(2),
                        r.IsDBNull(3) ? "" : r.GetString(3),
                        r.GetInt64(4),
                        r.IsDBNull(5) ? "allegato" : r.GetString(5),
                        r.IsDBNull(6) ? "application/octet-stream" : r.GetString(6),
                        r.IsDBNull(7) ? "" : r.GetString(7)
                    ));
                }

                return list;
            }
    private async Task<(string Email, string Password, string Host, int Port, bool UseSsl)?> LoadMailboxAsync(
    OracleConnection conn, int casellaId, CancellationToken ct)
    {
        const string sql = @"SELECT EMAIL, PASSWORD, IMAP_HOST, IMAP_PORT, USE_SSL
                           FROM SGAPP.CASELLEPOSTA
                          WHERE ID = :p_id";

        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
        cmd.Parameters.Add("p_id", OracleDbType.Int32).Value = casellaId;

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;

        var email = r.GetString(0);
        var pwd = r.GetString(1);
        var host = r.GetString(2);
        var port = r.GetInt32(3);
        var useSsl = string.Equals(r.GetString(4), "Y", StringComparison.OrdinalIgnoreCase);
        return (email, pwd, host, port, useSsl);
    }
    private async Task<IMailFolder?> GetFolderSafeAsync(ImapClient client, string folderPath, CancellationToken ct)
    {
        folderPath = (folderPath ?? "").Trim();
        if (string.IsNullOrWhiteSpace(folderPath))
            return client.Inbox;

        try { return await client.GetFolderAsync(folderPath, ct); }
        catch { }

        // fallback: cerca tra tutte le cartelle per FullName case-insensitive
        try
        {
            foreach (var ns in client.PersonalNamespaces)
            {
                var root = client.GetFolder(ns);
                var subs = await root.GetSubfoldersAsync(true, ct);
                var hit = subs.FirstOrDefault(f => string.Equals(f.FullName, folderPath, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit;
            }
        }
        catch { }

        return client.Inbox;
    }

    private async Task BackfillMissingAttachmentsAsync(CancellationToken ct)
    {
        var connString = _config.GetConnectionString("OracleDb")
                         ?? throw new InvalidOperationException("ConnectionString 'OracleDb' mancante.");

        var limit = _config.GetValue<int?>("Attachments:BackfillLimit") ?? 200;

        await using var conn = new OracleConnection(connString);
        await conn.OpenAsync(ct);

        var missing = await LoadMissingAttachmentsAsync(conn, limit, ct);
        if (missing.Count == 0)
            return;

        _logger.LogInformation("📎 Backfill allegati mancanti: {N} (limit={L})", missing.Count, limit);

        foreach (var byMailbox in missing.GroupBy(x => x.CasellaId))
        {
            var casellaId = byMailbox.Key;

            var acc = await LoadMailboxAsync(conn, casellaId, ct);
            if (acc == null)
            {
                _logger.LogWarning("📎 Backfill: casella {Id} non trovata", casellaId);
                continue;
            }

            using var proto = new ProtocolLogger(Stream.Null);
            using var client = new ImapClient(proto);

            client.AuthenticationMechanisms.Remove("XOAUTH2");
            var socket = acc.Value.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;

            try
            {
                await client.ConnectAsync(acc.Value.Host, acc.Value.Port, socket, ct);
                await client.AuthenticateAsync(acc.Value.Email, acc.Value.Password, ct);

                foreach (var byFolder in byMailbox.GroupBy(x => x.FolderPath ?? ""))
                {
                    var folder = await GetFolderSafeAsync(client, byFolder.Key, ct);
                    if (folder == null) continue;

                    await folder.OpenAsync(FolderAccess.ReadOnly, ct);

                    foreach (var a in byFolder)
                    {

                        if (a.MessageUid <= 0 || a.MessageUid > uint.MaxValue)
                        {
                            _logger.LogWarning("Backfill: UID non valido allegato={AllegatoId} uid={Uid}", a.AllegatoId, a.MessageUid);
                            continue;
                        }

                        var uid = new UniqueId((uint)a.MessageUid);

                        // scarico UN allegato usando la tua stessa logica path <casellaId>/<emailId>/...
                        await DownloadSingleAttachmentBackfillAsync(
                            folder,
                            uid,
                            a.CasellaId,
                            a.EmailId,
                            a.AllegatoId,
                            a.FileName,
                            a.PartSpec,
                            a.Mime,
                            conn,
                            ct);
                    }

                    await folder.CloseAsync(false, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "📎 Backfill: errore su casellaId={Id}", casellaId);
            }
            finally
            {
                try { if (client.IsConnected) await client.DisconnectAsync(true, ct); } catch { }
            }
        }
    }

    private async Task DownloadSingleAttachmentBackfillAsync(
    IMailFolder folder,
    UniqueId uid,
    int casellaId,
    int emailId,
    int allegatoId,
    string fileName,
    string partSpec,
    string mime,
    OracleConnection conn,
    CancellationToken ct)
    {
        string? filePath = null;

        try
        {
            var safeName = SanitizeFileName(string.IsNullOrWhiteSpace(fileName) ? "allegato" : fileName);

            var emailDir = Path.Combine(_attachmentsBasePath, casellaId.ToString(), emailId.ToString());
            Directory.CreateDirectory(emailDir);

            filePath = Path.Combine(emailDir, $"{allegatoId}_{safeName}");
            var relPath = Path.Combine(casellaId.ToString(), emailId.ToString(), $"{allegatoId}_{safeName}");

            // se file già c'è -> aggiorna db e stop (idempotente)
            if (File.Exists(filePath))
            {
                var fi = new FileInfo(filePath);
                await UpdateAttachmentPathAsync(conn, allegatoId, relPath, fi.Length,true, ct);
                return;
            }

            // fetch bodystructure del messaggio per trovare il part
            var summaries = await folder.FetchAsync(new[] { uid }, MessageSummaryItems.BodyStructure, ct);
            var body = summaries.FirstOrDefault()?.Body;
            if (body == null)
                return;

            var bp = FindBodyPartBySpecifier(body, partSpec);
            if (bp == null)
                return;

            var entity = await folder.GetBodyPartAsync(uid, bp, ct);

            long size;
            await using (var fs = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                if (entity is MimePart mp) await mp.Content.DecodeToAsync(fs, ct);
                else if (entity is MessagePart msgPart) await msgPart.Message.WriteToAsync(fs, ct);
                else await entity.WriteToAsync(fs, ct);

                await fs.FlushAsync(ct);
                size = fs.Length;
            }

            await UpdateAttachmentPathAsync(conn, allegatoId, relPath, size,true, ct);
        }
        catch
        {
            // cleanup file parziale
            try { if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath)) File.Delete(filePath); } catch { }
            throw;
        }
    }

    private static async Task UpdateAttachmentPathAsync(
    OracleConnection conn, int allegatoId, string relPath, long size, bool force, CancellationToken ct)
    {
        var sql = force ? @"
UPDATE SGAPP.EMAIL_ALLEGATI
   SET FILE_PATH = :p_path, FILE_SIZE = :p_size
 WHERE ID = :p_id"
        : @"
UPDATE SGAPP.EMAIL_ALLEGATI
   SET FILE_PATH = :p_path, FILE_SIZE = :p_size
 WHERE ID = :p_id
   AND FILE_PATH IS NULL";

        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
        cmd.Parameters.Add("p_path", OracleDbType.Varchar2, 1024).Value = relPath;
        cmd.Parameters.Add("p_size", OracleDbType.Int64).Value = size;
        cmd.Parameters.Add("p_id", OracleDbType.Int32).Value = allegatoId;
        await cmd.ExecuteNonQueryAsync(ct);
    }


    private async Task<List<IMessageSummary>> FetchBackfillBatchAsync(
    OracleConnection conn,
    IMailFolder folder,
    int casellaId,
    string folderPath,
    CancellationToken ct)
    {
        // cursor attuale (UID max ancora da fare)
        var cursor = await GetBackfillCursorAsync(conn, casellaId, folderPath, ct);

        // se non esiste ancora, inizializzo all'ultimo uid disponibile
        if (cursor is null)
        {
            if (!folder.UidNext.HasValue || folder.UidNext.Value.Id == 0)
                return new List<IMessageSummary>();

            var max = (long)folder.UidNext.Value.Id - 1;

            if (max <= 0)
                return new List<IMessageSummary>();

            cursor = max;

            await UpsertBackfillCursorAsync(conn, casellaId, folderPath, cursor.Value, ct);
        }

        if (cursor <= 0) return new();

        var end = (uint)Math.Min(cursor.Value, uint.MaxValue);
        var start = end > MaxPerFolderPerRun ? end - (uint)MaxPerFolderPerRun + 1 : 1;
        _logger.LogDebug(
            "BACKFILL cid={Cid} folder='{Folder}' cursor={Cursor} start={Start} end={End}",
            casellaId,
            NormalizeFolderPath(folderPath),
            cursor,
            start,
            end
        );
        var range = new UniqueIdRange(new UniqueId(start), new UniqueId(end));

        var items = MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope |
                    MessageSummaryItems.InternalDate | MessageSummaryItems.BodyStructure;

        var summaries = await folder.FetchAsync(range, items, ct);

        // aggiorno cursor a "prima del blocco"
        var nextCursor = (long)start - 1;
        await UpsertBackfillCursorAsync(conn, casellaId, folderPath, nextCursor, ct);

        // ordino dal vecchio al nuovo (così logica invariata)
        return summaries.OrderBy(s => s.UniqueId.Id).ToList();
    }
    private async Task<long?> GetBackfillCursorAsync(OracleConnection conn, int casellaId, string folderPath, CancellationToken ct)
    {
        const string sql = @"
SELECT BACKFILL_UID
  FROM SGAPP.EMAIL_SYNC_STATE
 WHERE CASELLA_ID = :p_cid
   AND FOLDER_PATH = :p_fp";
        var folderCorrect = NormalizeFolderPath(folderPath);
        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
        cmd.Parameters.Add("p_cid", OracleDbType.Int32).Value = casellaId;
        cmd.Parameters.Add("p_fp", OracleDbType.Varchar2, 512).Value = folderCorrect;
        var obj = await cmd.ExecuteScalarAsync(ct);
        if (obj == null || obj == DBNull.Value) return null;
        return Convert.ToInt64(obj);
    }
    private static string NormalizeFolderPath(string folderPath)
    {
        folderPath = (folderPath ?? "").Trim();
        if (folderPath.Length > 512) folderPath = folderPath[..512];
        return folderPath;
    }

    private async Task UpsertBackfillCursorAsync(OracleConnection conn, int casellaId, string folderPath, long cursor, CancellationToken ct)
    {
        const string sql = @"
MERGE INTO SGAPP.EMAIL_SYNC_STATE t
USING (SELECT :p_cid AS CASELLA_ID, :p_fp AS FOLDER_PATH, :p_uid AS BACKFILL_UID FROM dual) s
ON (t.CASELLA_ID = s.CASELLA_ID AND t.FOLDER_PATH = s.FOLDER_PATH)
WHEN MATCHED THEN UPDATE SET t.BACKFILL_UID = s.BACKFILL_UID, t.UPDATED_AT = SYSDATE
WHEN NOT MATCHED THEN INSERT (CASELLA_ID, FOLDER_PATH, BACKFILL_UID, UPDATED_AT)
VALUES (s.CASELLA_ID, s.FOLDER_PATH, s.BACKFILL_UID, SYSDATE)";

        var folderCorrect = NormalizeFolderPath(folderPath);

        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
        cmd.Parameters.Add("p_cid", OracleDbType.Int32).Value = casellaId;
        cmd.Parameters.Add("p_fp", OracleDbType.Varchar2, 512).Value = folderCorrect;
        cmd.Parameters.Add("p_uid", OracleDbType.Int64).Value = cursor;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<int?> GetExistingEmailIdByMessageId(
    OracleConnection conn, int casellaId, string messageId, CancellationToken ct)
    {
        const string sql = @"
SELECT ID
  FROM SGAPP.EMAIL_RICEVUTE
 WHERE CASELLA_ID = :p_cid
   AND MESSAGE_ID = :p_mid
 FETCH FIRST 1 ROWS ONLY";

        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
        cmd.Parameters.Add("p_cid", OracleDbType.Int32).Value = casellaId;
        cmd.Parameters.Add("p_mid", OracleDbType.Varchar2, 500).Value = messageId;

        var obj = await cmd.ExecuteScalarAsync(ct);
        if (obj == null || obj == DBNull.Value) return null;

        if (obj is Oracle.ManagedDataAccess.Types.OracleDecimal od) return od.ToInt32();
        return Convert.ToInt32(obj);
    }

    private async Task<long?> GetLastSeenUidAsync(OracleConnection conn, int casellaId, string folderPath, CancellationToken ct)
    {
        const string sql = @"
SELECT LAST_SEEN_UID
  FROM SGAPP.EMAIL_SYNC_STATE
 WHERE CASELLA_ID = :p_cid
   AND FOLDER_PATH = :p_fp";

        folderPath = (folderPath ?? "").Trim();
        if (folderPath.Length > 512) folderPath = folderPath[..512];

        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
        cmd.Parameters.Add("p_cid", OracleDbType.Int32).Value = casellaId;
        cmd.Parameters.Add("p_fp", OracleDbType.Varchar2, 512).Value = folderPath;

        var obj = await cmd.ExecuteScalarAsync(ct);
        if (obj == null || obj == DBNull.Value) return null;
        return Convert.ToInt64(obj);
    }

    private async Task UpsertLastSeenUidAsync(OracleConnection conn, int casellaId, string folderPath, long lastSeenUid, CancellationToken ct)
    {
        const string sql = @"
MERGE INTO SGAPP.EMAIL_SYNC_STATE t
USING (SELECT :p_cid AS CASELLA_ID, :p_fp AS FOLDER_PATH, :p_uid AS LAST_SEEN_UID FROM dual) s
ON (t.CASELLA_ID = s.CASELLA_ID AND t.FOLDER_PATH = s.FOLDER_PATH)
WHEN MATCHED THEN UPDATE SET t.LAST_SEEN_UID = s.LAST_SEEN_UID, t.UPDATED_AT = SYSDATE
WHEN NOT MATCHED THEN INSERT (CASELLA_ID, FOLDER_PATH, LAST_SEEN_UID, UPDATED_AT)
VALUES (s.CASELLA_ID, s.FOLDER_PATH, s.LAST_SEEN_UID, SYSDATE)";

        folderPath = (folderPath ?? "").Trim();
        if (folderPath.Length > 512) folderPath = folderPath[..512];

        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
        cmd.Parameters.Add("p_cid", OracleDbType.Int32).Value = casellaId;
        cmd.Parameters.Add("p_fp", OracleDbType.Varchar2, 512).Value = folderPath;
        cmd.Parameters.Add("p_uid", OracleDbType.Int64).Value = lastSeenUid;

        await cmd.ExecuteNonQueryAsync(ct);
    }
    private async Task<List<IMessageSummary>> FetchNewBatchAsync(
     OracleConnection conn,
     IMailFolder folder,
     int casellaId,
     string folderPath,
     CancellationToken ct,
     bool nightMode)
    {
        var folderCorrect = NormalizeFolderPath(folderPath);
        var lastSeen = await GetLastSeenUidAsync(conn, casellaId, folderCorrect, ct) ?? 0L;
        folderPath = (folderPath ?? "").Trim();

        // ultimo UID già processato "in avanti"

        if (!folder.UidNext.HasValue || folder.UidNext.Value.Id == 0)
            return new();

        var end = (long)folder.UidNext.Value.Id - 1;
        if (lastSeen == 0 && end > 0)
        {
            if (!nightMode)
            {
                // Giorno: mi allineo e NON scarico lo storico
                await UpsertLastSeenUidAsync(conn, casellaId, folderCorrect, end, ct);

                _logger.LogInformation(
                    "BOOTSTRAP (giorno) cid={Cid} folder='{Folder}' setLastSeen={End} (skip storico)",
                    casellaId, folderCorrect, end
                );

                return new();
            }
        }
        var start = lastSeen + 1;

        if (start > end)
            return new();

        // Limita a MaxPerFolderPerRun (fairness)
        var limitedEnd = Math.Min(end, start + MaxPerFolderPerRun - 1);

        if (start <= 0 || limitedEnd <= 0) return new();
        if (start > uint.MaxValue) return new();

        var uStart = (uint)start;
        var uEnd = (uint)Math.Min(limitedEnd, uint.MaxValue);

        var range = new UniqueIdRange(new UniqueId(uStart), new UniqueId(uEnd));

        var items = MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope |
                    MessageSummaryItems.InternalDate | MessageSummaryItems.BodyStructure;
        _logger.LogInformation(
                "NEWSCAN cid={Cid} folder='{Folder}' lastSeen={LastSeen} uidNext={UidNext} start={Start} end={End} limitEnd={LimitEnd}",
                casellaId,
                folderCorrect,
                lastSeen,
                folder.UidNext.Value.Id,
                start,
                end,
                limitedEnd
            );
        var summaries = await folder.FetchAsync(range, items, ct);

        if (summaries.Count > 0)
        {
            var maxUidRead = summaries.Max(s => (long)s.UniqueId.Id);
            await UpsertLastSeenUidAsync(conn, casellaId, folderCorrect, maxUidRead, ct);
        }

        return summaries.OrderBy(s => s.UniqueId.Id).ToList();
    }
    private async Task<List<IMessageSummary>> FetchTodayBatchAsync(
        OracleConnection conn,
        IMailFolder folder,
        int casellaId,
        string folderPath,
        CancellationToken ct)
    {
        var folderCorrect = NormalizeFolderPath(folderPath);

        // mezzanotte di Roma -> UTC
        var todayRomeDate = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, RomeTz).Date;
        var midnightRome = new DateTimeOffset(todayRomeDate, RomeTz.GetUtcOffset(todayRomeDate));
        var sinceTodayUtc = midnightRome.UtcDateTime;

        // ✅ finestra extra: ultime 3 ore prima della mezzanotte (late yesterday)
        var sinceLateYesterdayUtc = sinceTodayUtc.AddHours(-3);

        // 1) UID di oggi
        IList<UniqueId> uidsToday;
        try
        {
            uidsToday = await folder.SearchAsync(SearchQuery.DeliveredAfter(sinceTodayUtc), ct);
        }
        catch
        {
            return new List<IMessageSummary>();
        }

        // 2) UID “late yesterday”
        IList<UniqueId> uidsLateYesterday = Array.Empty<UniqueId>();
        try
        {
            uidsLateYesterday = await folder.SearchAsync(SearchQuery.DeliveredAfter(sinceLateYesterdayUtc), ct);
        }
        catch
        {
            uidsLateYesterday = Array.Empty<UniqueId>();
        }

        // Unione + dedup
        var uids = uidsToday.Concat(uidsLateYesterday)
                            .Distinct()
                            .OrderBy(u => u.Id)
                            .ToList();

        if (uids.Count == 0) return new List<IMessageSummary>();

        // ✅ evita riprocessare uid già visti (usa LAST_SEEN_UID)
        var lastSeen = await GetLastSeenUidAsync(conn, casellaId, folderCorrect, ct) ?? 0L;
        if (lastSeen > 0 && lastSeen < long.MaxValue)
        {
            var cut = (uint)Math.Min(lastSeen, uint.MaxValue);
            uids = uids.Where(u => u.Id > cut).ToList();
        }

        if (uids.Count == 0) return new List<IMessageSummary>();

        // fairness: blocco principale + garantisco “ultime 3”
        var baseTake = (uids.Count > MaxPerFolderPerRun)
            ? uids.Skip(uids.Count - MaxPerFolderPerRun).ToList()
            : uids.ToList();

        var last3 = uids.Count <= 3 ? uids : uids.Skip(uids.Count - 3).ToList();

        var finalUids = baseTake.Concat(last3)
                                .Distinct()
                                .OrderBy(u => u.Id)
                                .ToList();

        var items = MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope |
                    MessageSummaryItems.InternalDate | MessageSummaryItems.BodyStructure;

        var sums = await folder.FetchAsync(finalUids, items, ct);
        if (sums == null || sums.Count == 0) return new List<IMessageSummary>();

        // ✅ extra-check SOLO sulle ultime 3: se già in DB, le scarto
        var sumsOrdered = sums.OrderBy(s => s.UniqueId.Id).ToList();
        var last3Fetched = sumsOrdered.Where(s => last3.Any(u => u.Id == s.UniqueId.Id)).ToList();

        var keep = new List<IMessageSummary>();
        foreach (var s in last3Fetched)
        {
            var msg = await folder.GetMessageAsync(s.UniqueId, ct);
            var mid = msg.MessageId?.Trim();
            if (string.IsNullOrWhiteSpace(mid))
                mid = BuildStableFallbackMessageId(msg);

            var exists = await GetExistingEmailIdByMessageId(conn, casellaId, mid, ct);
            if (!exists.HasValue)
                keep.Add(s);
        }

        var baseSummaries = sumsOrdered.Where(s => !last3.Any(u => u.Id == s.UniqueId.Id)).ToList();
        var finalSummaries = baseSummaries.Concat(keep).OrderBy(s => s.UniqueId.Id).ToList();

        // ✅ IMPORTANTISSIMO: aggiorna lastSeen sul massimo UID CHE HAI VISTO (fetchato),
        // anche se poi non hai nulla da processare (così non ripeti sempre le stesse).
        var maxFetched = sumsOrdered.Max(s => (long)s.UniqueId.Id);
        await UpsertLastSeenUidAsync(conn, casellaId, folderCorrect, maxFetched, ct);

        return finalSummaries; // può essere vuota, ed è ok
    }

    public async Task<bool> EnsureSingleAttachmentAsync(int allegatoId, CancellationToken ct)
    {
        var sem = _ensureLocks.GetOrAdd(allegatoId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            var cs = _config.GetConnectionString("OracleDb")
                     ?? throw new InvalidOperationException("ConnectionString 'OracleDb' mancante.");

            await using var conn = new OracleConnection(cs);
            await conn.OpenAsync(ct);

            // 1) carica tutto quel che serve (join allegato + email)
            var info = await LoadEnsureInfoAsync(conn, allegatoId, ct);
            if (info == null) return false;

            // 2) se già su disco -> OK
            var existingFull = TryResolveExistingPath(info);
            if (existingFull != null && File.Exists(existingFull))
                return true;

            // 3) credenziali casella
            var acc = await LoadMailboxAsync(conn, info.CasellaId, ct);
            if (acc == null) return false;

            // 4) scarica da IMAP e aggiorna DB
            await DownloadSingleAttachmentFromImapAsync(conn, info, acc.Value, ct);
            return true;
        }
        finally
        {
            sem.Release();
            // opzionale: cleanup dict per non crescere all’infinito
            if (sem.CurrentCount == 1) _ensureLocks.TryRemove(allegatoId, out _);
        }
    }

    private sealed record EnsureInfo(
        int AllegatoId,
        int EmailId,
        int CasellaId,
        string FolderPath,
        long MessageUid,
        string FileName,
        string Mime,
        string PartSpec,
        string? FilePath
    );

    private async Task<EnsureInfo?> LoadEnsureInfoAsync(OracleConnection conn, int allegatoId, CancellationToken ct)
    {
        const string sql = @"
SELECT a.ID AS ALLEGATO_ID,
       a.EMAIL_ID,
       e.CASELLA_ID,
       e.FOLDER_PATH,
       e.MESSAGE_UID,
       a.NOME_FILE,
       a.MIME_TYPE,
       a.PART_SPEC,
       a.FILE_PATH
  FROM SGAPP.EMAIL_ALLEGATI a
  JOIN SGAPP.EMAIL_RICEVUTE e ON e.ID = a.EMAIL_ID
 WHERE a.ID = :p_id";

        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
        cmd.Parameters.Add("p_id", OracleDbType.Int32).Value = allegatoId;

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;

        return new EnsureInfo(
            AllegatoId: r.GetInt32(0),
            EmailId: r.GetInt32(1),
            CasellaId: r.GetInt32(2),
            FolderPath: r.IsDBNull(3) ? "" : r.GetString(3),
            MessageUid: r.IsDBNull(4) ? 0L : r.GetInt64(4),
            FileName: r.IsDBNull(5) ? "allegato" : r.GetString(5),
            Mime: r.IsDBNull(6) ? "application/octet-stream" : r.GetString(6),
            PartSpec: r.IsDBNull(7) ? "" : r.GetString(7),
            FilePath: r.IsDBNull(8) ? null : r.GetString(8)
        );
    }

    private string? TryResolveExistingPath(EnsureInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.FilePath)) return null;
        var rel = info.FilePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(_attachmentsBasePath, rel));
        var baseFull = Path.GetFullPath(_attachmentsBasePath);
        if (!baseFull.EndsWith(Path.DirectorySeparatorChar)) baseFull += Path.DirectorySeparatorChar;
        return full.StartsWith(baseFull, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private async Task DownloadSingleAttachmentFromImapAsync(
        OracleConnection conn,
        EnsureInfo info,
        (string Email, string Password, string Host, int Port, bool UseSsl) mailbox,
        CancellationToken ct)
    {
        if (info.MessageUid <= 0 || info.MessageUid > uint.MaxValue)
            throw new InvalidOperationException($"MESSAGE_UID non valido per allegato={info.AllegatoId}: {info.MessageUid}");

        var safeName = SanitizeFileName(string.IsNullOrWhiteSpace(info.FileName) ? "allegato" : info.FileName);

        var emailDir = Path.Combine(_attachmentsBasePath, info.CasellaId.ToString(), info.EmailId.ToString());
        Directory.CreateDirectory(emailDir);

        var filePath = Path.Combine(emailDir, $"{info.AllegatoId}_{safeName}");
        var relPath = Path.Combine(info.CasellaId.ToString(), info.EmailId.ToString(), $"{info.AllegatoId}_{safeName}");

        // idempotente
        if (File.Exists(filePath))
        {
            var fi = new FileInfo(filePath);
            await UpdateAttachmentPathIfMissingAsync(conn, info.AllegatoId, relPath, fi.Length, ct);
            return;
        }

        using var proto = new ProtocolLogger(Stream.Null);
        using var client = new ImapClient(proto);
        client.AuthenticationMechanisms.Remove("XOAUTH2");

        var socket = mailbox.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;

        await client.ConnectAsync(mailbox.Host, mailbox.Port, socket, ct);
        await client.AuthenticateAsync(mailbox.Email, mailbox.Password, ct);

        var folder = await GetFolderSafeAsync(client, info.FolderPath, ct) ?? client.Inbox;
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);

        var uid = new UniqueId((uint)info.MessageUid);

        var sums = await folder.FetchAsync(new[] { uid }, MessageSummaryItems.BodyStructure, ct);
        var body = sums.FirstOrDefault()?.Body;
        if (body == null)
            throw new InvalidOperationException("BodyStructure NULL (messaggio non trovato / non accessibile).");

        var bp = FindBodyPartBySpecifier(body, info.PartSpec);
        if (bp == null)
            throw new InvalidOperationException($"BodyPart non trovato per PartSpec='{info.PartSpec}'");

        var entity = await folder.GetBodyPartAsync(uid, bp, ct);
        if (entity == null)
            throw new InvalidOperationException("GetBodyPartAsync ha restituito NULL.");

        long size;
        await using (var fs = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            if (entity is MimePart mp) await mp.Content.DecodeToAsync(fs, ct);
            else if (entity is MessagePart msgPart) await msgPart.Message.WriteToAsync(fs, ct);
            else await entity.WriteToAsync(fs, ct);

            await fs.FlushAsync(ct);
            size = fs.Length;
        }

        await UpdateAttachmentPathIfMissingAsync(conn, info.AllegatoId, relPath, size, ct);

        await folder.CloseAsync(false, ct);
        await client.DisconnectAsync(true, ct);
    }

    private static async Task UpdateAttachmentPathIfMissingAsync(
        OracleConnection conn,
        int allegatoId,
        string relPath,
        long size,
        CancellationToken ct)
    {
        const string upd = @"
UPDATE SGAPP.EMAIL_ALLEGATI
   SET FILE_PATH = :p_path,
       FILE_SIZE = :p_size
 WHERE ID = :p_id
   AND FILE_PATH IS NULL";

        await using var cmd = new OracleCommand(upd, conn) { BindByName = true };
        cmd.Parameters.Add("p_path", OracleDbType.Varchar2, 1024).Value = relPath;
        cmd.Parameters.Add("p_size", OracleDbType.Int64).Value = size;
        cmd.Parameters.Add("p_id", OracleDbType.Int32).Value = allegatoId;

        await cmd.ExecuteNonQueryAsync(ct);
    }

}
