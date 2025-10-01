using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MimeKit;
using Oracle.ManagedDataAccess.Client;

public class EmailFetchService : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly ILogger<EmailFetchService> _logger;

    // Quante mail max per cartella ad ogni passata (fairness)
    private const int MaxPerFolderPerRun = 50;

    // Ogni quanto ripassare
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(1);

    public EmailFetchService(IConfiguration config, ILogger<EmailFetchService> logger)
    {
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("EmailFetchService avviato - intervallo {Minuti} minuti", _interval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessAllMailboxes(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Errore generale nel fetch email");
            }

            try { await Task.Delay(_interval, stoppingToken); } catch { /* ignore */ }
        }
    }

    public async Task ProcessAllMailboxes(CancellationToken ct)
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

                await FetchEmailsForAccount(a.Id, a.Email, a.Password, a.Host, a.Port, a.UseSsl, accountConn, ct);

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
     OracleConnection dbConn, CancellationToken ct)
    {
        if (casellaId <= 0) return;

        // Log di protocollo IMAP su file (nella cartella dell’eseguibile)
        using var proto = new ProtocolLogger(Path.Combine(AppContext.BaseDirectory, $"imap_{email.Replace("@", "_")}.log"));
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
            var folder = client.Inbox;
            await folder.StatusAsync(status, ct);

            await folder.OpenAsync(FolderAccess.ReadOnly, ct);

            var lastUid = await GetLastSavedUid(dbConn, casellaId, folder.FullName, ct);
            var startUid = lastUid > 0 ? new UniqueId((uint)(lastUid + 1)) : UniqueId.MinValue;
            var range = new UniqueIdRange(startUid, UniqueId.MaxValue);

            var summaries = await folder.FetchAsync(range,
                MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope |
                MessageSummaryItems.InternalDate | MessageSummaryItems.BodyStructure, ct);

            var toProcess = summaries.OrderBy(s => s.UniqueId.Id).Take(MaxPerFolderPerRun).ToList();
            if (toProcess.Count == 0)
            {
                _logger.LogInformation("Inbox {Email}: nessun nuovo messaggio (lastUid={Last})", email, lastUid);
            }
            else
            {
                _logger.LogInformation("Inbox '{Folder}': nuovi UID {First}..{Last} (elaboro {N}/{Tot})",
                    folder.FullName, toProcess.First().UniqueId.Id, toProcess.Last().UniqueId.Id, toProcess.Count, summaries.Count);

                foreach (var s in toProcess)
                {
                    var uid = (long)s.UniqueId.Id;
                    if (await EmailExists(dbConn, casellaId, folder.FullName, uid, ct)) continue;

                    var full = await folder.GetMessageAsync(s.UniqueId, ct);
                    try
                    {
                        var emailId = await SaveEmail(dbConn, casellaId,
                            s.Envelope?.MessageId ?? Guid.NewGuid().ToString(),
                            full, ct, folder.FullName, uid, s.InternalDate?.UtcDateTime);

                        _logger.LogInformation("Salvata email ID={Id} [{Acc}] {Folder} UID={Uid} Subj='{Subj}'",
                            emailId, email, folder.FullName, uid, full.Subject);

                        await SaveAttachmentsMetadata(dbConn, emailId, s.Body, ct);
                    }
                    catch (Oracle.ManagedDataAccess.Client.OracleException ex) when (ex.Number == 1) // ORA-00001
                    {
                        _logger.LogDebug("Duplicato ignorato (CASELLA_ID={Id}, FOLDER={Folder}, UID={Uid})", casellaId, folder.FullName, uid);
                    }
                }
            }

            await folder.CloseAsync(false, ct);
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
            proto.Dispose(); // flush file log
            _logger.LogInformation("Disconnesso da {Email}", email);
        }
    }



    private static async Task GatherAsync(
        IMailFolder folder, List<IMailFolder> acc, StatusItems status, CancellationToken ct)
    {
        if ((folder.Attributes & FolderAttributes.NonExistent) != 0)
            return;

        if ((folder.Attributes & FolderAttributes.NoSelect) == 0)
        {
            try { await folder.StatusAsync(status, ct); acc.Add(folder); }
            catch { /* ignore */ }
        }

        var children = await folder.GetSubfoldersAsync(false, ct);
        foreach (var child in children)
            await GatherAsync(child, acc, status, ct);
    }

    // === DB helpers ==========================================================

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
        const string sql = @"
            INSERT INTO SGAPP.EMAIL_RICEVUTE
                (CASELLA_ID, MESSAGE_ID, DATA_RICEZIONE, MITTENTE, DESTINATARI, OGGETTO,
                 CORPO_HTML, CORPO_TESTO, APERTO, ELIMINATO, FOLDER_PATH, MESSAGE_UID)
            VALUES
                (:p_cid, :p_mid, :p_dt, :p_from, :p_to, :p_subj,
                 :p_html, :p_text, 'N', 'N', :p_fp, :p_uid)
            RETURNING ID INTO :p_id";

        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };

        cmd.Parameters.Add("p_cid", OracleDbType.Int32).Value = casellaId;
        cmd.Parameters.Add("p_mid", OracleDbType.Varchar2, 500).Value = messageId;
        cmd.Parameters.Add("p_dt", OracleDbType.Date).Value = (internalDateUtc ?? message.Date.UtcDateTime);
        cmd.Parameters.Add("p_from", OracleDbType.Varchar2, 500).Value = message.From?.ToString() ?? "";
        cmd.Parameters.Add("p_to", OracleDbType.Varchar2, 2000).Value = message.To?.ToString() ?? "";
        cmd.Parameters.Add("p_subj", OracleDbType.Varchar2, 1000).Value = message.Subject ?? "";
        cmd.Parameters.Add("p_html", OracleDbType.Clob).Value = (object?)message.HtmlBody ?? DBNull.Value;
        cmd.Parameters.Add("p_text", OracleDbType.Clob).Value = (object?)message.TextBody ?? DBNull.Value;
        cmd.Parameters.Add("p_fp", OracleDbType.Varchar2, 512).Value =
            string.IsNullOrEmpty(folderPath) ? "" :
            (folderPath.Length <= 512 ? folderPath : folderPath[..512]);
        cmd.Parameters.Add("p_uid", OracleDbType.Int64).Value = (object?)messageUid ?? DBNull.Value;

        var outId = new OracleParameter("p_id", OracleDbType.Int32) { Direction = ParameterDirection.Output };
        cmd.Parameters.Add(outId);

        await cmd.ExecuteNonQueryAsync(ct);

        if (outId.Value is Oracle.ManagedDataAccess.Types.OracleDecimal o) return o.ToInt32();
        return Convert.ToInt32(outId.Value?.ToString());
    }

    private async Task SaveAttachmentsMetadata(OracleConnection conn, int emailId, BodyPart? body, CancellationToken ct)
    {
        if (body is null) return;

        var list = new List<(string FileName, string Mime, string PartSpec)>();
        CollectAttachmentParts(body, list);

        foreach (var a in list)
        {
            const string sql = @"
                INSERT INTO SGAPP.EMAIL_ALLEGATI (EMAIL_ID, NOME_FILE, MIME_TYPE, PART_SPEC)
                VALUES (:p_eid, :p_name, :p_mime, :p_part)";

            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
            cmd.Parameters.Add("p_eid", OracleDbType.Int32).Value = emailId;
            cmd.Parameters.Add("p_name", OracleDbType.Varchar2, 512).Value = a.FileName ?? "allegato";
            cmd.Parameters.Add("p_mime", OracleDbType.Varchar2, 255).Value = a.Mime ?? "application/octet-stream";
            cmd.Parameters.Add("p_part", OracleDbType.Varchar2, 64).Value = a.PartSpec ?? "";

            await cmd.ExecuteNonQueryAsync(ct);
            _logger.LogInformation("Allegato registrato: {NomeFile} ({Part})", a.FileName, a.PartSpec);
        }
    }

    private static void CollectAttachmentParts(BodyPart part, List<(string FileName, string Mime, string PartSpec)> acc)
    {
        if (part is BodyPartBasic basic)
        {
            var fileName = basic.FileName;
            var disp = basic.ContentDisposition?.Disposition;

            var isAttachment = !string.IsNullOrEmpty(fileName) ||
                               string.Equals(disp, "attachment", StringComparison.OrdinalIgnoreCase);

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
}
