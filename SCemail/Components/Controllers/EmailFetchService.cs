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

    public EmailFetchService(
        IConfiguration config,
        ILogger<EmailFetchService> logger)
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
        var accounts = new List<(int Id, string Email, string Password, string Provider, string Host, int Port, bool UseSsl)>();
        await using (var conn = new OracleConnection(connString))
        {
            await conn.OpenAsync(ct);

            const string sql = @"
SELECT ID, EMAIL, PASSWORD,PROVIDER, IMAP_HOST, IMAP_PORT, USE_SSL
FROM SGAPP.CASELLEPOSTA
WHERE NVL(ATTIVA, 'Y') = 'Y'";
            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetInt32(0);
                var email = reader.GetString(1);
                var pwd = reader.GetString(2);
                var provider = reader.IsDBNull(3) ? "IMAP" : reader.GetString(3);
                var host = reader.GetString(4);
                var port = reader.GetInt32(5);
                var useSsl = string.Equals(reader.GetString(6), "Y", StringComparison.OrdinalIgnoreCase);

                accounts.Add((id, email, pwd, provider, host, port, useSsl));
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

                await FetchEmailsForAccount(a.Id, a.Email, a.Password, a.Provider, a.Host, a.Port, a.UseSsl, accountConn, ct, nightMode);

                _logger.LogInformation("=== FINE CASELLA {Email} ===", a.Email);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Errore nella casella {Email}", a.Email);
            }
        }
    }
    private static string? NormalizeMessageId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var v = value.Trim();

        if (v.StartsWith("<") && v.EndsWith(">") && v.Length > 2)
            v = v[1..^1];

        v = v.Trim('<', '>', ' ', '\t', '\r', '\n');

        return string.IsNullOrWhiteSpace(v)
            ? null
            : v.Trim();
    }

    private static List<string> ExtractMessageIdsFromReferences(string? refs)
    {
        if (string.IsNullOrWhiteSpace(refs))
            return new();

        var result = new List<string>();

        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(refs, @"<([^>]+)>"))
        {
            var id = NormalizeMessageId(m.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(id))
                result.Add(id);
        }

        if (result.Count == 0)
        {
            result = refs
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeMessageId)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!)
                .ToList();
        }

        return result
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> GetThreadCandidateMessageIds(MimeMessage message)
    {
        var ids = new List<string>();

        var inReplyTo = NormalizeMessageId(message.InReplyTo);
        if (!string.IsNullOrWhiteSpace(inReplyTo))
            ids.Add(inReplyTo);

        if (message.References != null && message.References.Any())
        {
            ids.AddRange(
                message.References
                    .Select(NormalizeMessageId)
                    .Where(x => !string.IsNullOrWhiteSpace(x))!
            );
        }

        return ids
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Reverse()
            .ToList();
    }
    private sealed class ThreadResolveResult
    {
        public string ThreadKey { get; init; } = "";
        public string? GmailThreadKey { get; init; }
    }

    private async Task<ThreadResolveResult> ResolveThreadKeyAsync(
    OracleConnection conn,
    int casellaId,
    string currentMessageId,
    MimeMessage message,
    string? providerThreadKey,
    CancellationToken ct)
    {
        currentMessageId = NormalizeMessageId(currentMessageId) ?? currentMessageId;

        var providerThreadKeyNorm = (providerThreadKey ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(providerThreadKeyNorm))
        {
            // Per Gmail il thread id del provider è la chiave più affidabile e stabile.
            return new ThreadResolveResult
            {
                ThreadKey = providerThreadKeyNorm,
                GmailThreadKey = providerThreadKeyNorm
            };
        }

        const string sqlFindByMessageId = @"
SELECT THREAD_KEY, GMAIL_THREAD_ID
FROM (
    SELECT MESSAGE_ID,
           THREAD_KEY,
           GMAIL_THREAD_ID,
           DATA_RICEZIONE AS DATA_REF
      FROM SGAPP.EMAIL_RICEVUTE
     WHERE CASELLA_ID = :p_cid
       AND LOWER(MESSAGE_ID) = :p_mid

    UNION ALL

    SELECT MESSAGE_ID,
           THREAD_KEY,
           GMAIL_THREAD_ID,
           DATA_INVIO AS DATA_REF
      FROM SGAPP.EMAIL_INVIATE
     WHERE CASELLA_ID = :p_cid
       AND LOWER(MESSAGE_ID) = :p_mid
)
ORDER BY DATA_REF DESC
FETCH FIRST 1 ROWS ONLY";

        async Task<ThreadResolveResult?> FindParentAsync(string? rawMessageId)
        {
            var mid = NormalizeMessageId(rawMessageId);
            if (string.IsNullOrWhiteSpace(mid))
                return null;

            await using var cmd = new OracleCommand(sqlFindByMessageId, conn)
            {
                BindByName = true
            };

            cmd.Parameters.Add("p_cid", OracleDbType.Int32).Value = casellaId;
            cmd.Parameters.Add("p_mid", OracleDbType.Varchar2, 500).Value = mid.ToLowerInvariant();

            await using var r = await cmd.ExecuteReaderAsync(ct);

            if (!await r.ReadAsync(ct))
                return null;

            var parentThreadKey = r.IsDBNull(0) ? null : r.GetString(0);
            var parentGmailThreadId = r.IsDBNull(1) ? null : r.GetString(1);

            if (string.IsNullOrWhiteSpace(parentThreadKey))
                parentThreadKey = mid;

            return new ThreadResolveResult
            {
                ThreadKey = parentThreadKey,
                GmailThreadKey = parentGmailThreadId
            };
        }

        foreach (var candidate in GetThreadCandidateMessageIds(message))
        {
            var found = await FindParentAsync(candidate);
            if (found != null)
                return found;
        }

        return new ThreadResolveResult
        {
            ThreadKey = currentMessageId,
            GmailThreadKey = providerThreadKey
        };
    }
    private async Task FetchEmailsForAccount(
     int casellaId, string email, string password, string provider, string host, int port, bool useSsl,
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
            var seenMessageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // ✅ da qui in poi il tuo codice “solo INBOX” (già deduplicato)
            var status = StatusItems.Count | StatusItems.Recent | StatusItems.Unread;

            // 1) raccolgo inbox + tutte le sottocartelle
            var foldersToProcess = new List<IMailFolder>();

            foldersToProcess.Add(client.Inbox);

            TryAddSpecial(client, SpecialFolder.Sent, foldersToProcess, email);
            TryAddSpecial(client, SpecialFolder.Junk, foldersToProcess, email);
            TryAddGmailAllMail(client, foldersToProcess, email);
            // dedup + noselect
            foldersToProcess = foldersToProcess
                 .Where(f => f != null)
                 .Where(f => !string.IsNullOrWhiteSpace(f.FullName))
                 .Where(f => (f.Attributes & FolderAttributes.NoSelect) == 0)
                 .Where(f => !IsDraftFolder(f)) // ✅ NON leggere bozze
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
                var isSentFolder = IsSentFolder(folder);
                var isSpamFolder = IsSpamFolder(folder);
                if (!nightMode)
                {
                    // GIORNO: solo oggi (anche All/Spam)
                    newSummaries = await FetchTodayBatchAsync(dbConn, folder, casellaId, folder.FullName, ct, isSentFolder);
                    _logger.LogInformation("DAYSCAN cid={Cid} folder='{Folder}' oggi={N}", casellaId, folder.FullName, newSummaries.Count);
                }
                else
                {
                    // NOTTE: nuove a UID
                    newSummaries = await FetchNewBatchAsync(dbConn, folder, casellaId, folder.FullName, ct, nightMode);
                    if (newSummaries.Count > 0)
                        _logger.LogInformation("🆕 '{Folder}': nuove da processare={N}", folder.FullName, newSummaries.Count);
                }
                await ProcessSummariesAsync(folder, newSummaries, dbConn, casellaId, email, provider,seenMessageIds, ct, isSentFolder, isSpamFolder);
                // ✅ B) BACKFILL SOLO DI NOTTE (UNA VOLTA)
                if (nightMode)
                {
                    var backSummaries = await FetchBackfillBatchAsync(dbConn, folder, casellaId, folder.FullName, ct);
                    if (backSummaries.Count > 0)
                        _logger.LogInformation("⏪ '{Folder}': backfill da processare={N}", folder.FullName, backSummaries.Count);

                    await ProcessSummariesAsync(folder, backSummaries, dbConn, casellaId, email, provider, seenMessageIds, ct, isSentFolder, isSpamFolder);
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
            if (f != null)
            {
                list.Add(f);
                return;
            }
        }
        catch { }

        var fallbackNames = special switch
        {
            SpecialFolder.Sent => new[]
            {
            "Sent",
            "Sent Mail",
            "INBOX.Sent",
            "INBOX.Sent Mail",
            "Posta inviata",
            "Inviata",
            "[Gmail]/Posta inviata",
            "[Gmail]/Sent Mail"
        },

            SpecialFolder.Junk => new[]
            {
            "INBOX.Spam",
            "Spam",
            "Junk",
            "Posta indesiderata"
        },

            _ => Array.Empty<string>()
        };

        foreach (var name in fallbackNames)
        {
            try
            {
                var f = client.GetFolder(name);
                if (f != null)
                {
                    list.Add(f);
                    return;
                }
            }
            catch { }
        }

        _logger.LogDebug("SpecialFolder {Special} non trovato per {Email}", special, email);
    }

    private static bool IsSentFolder(IMailFolder folder)
    {
        var name = NormalizeFolderPath(folder.FullName).ToLowerInvariant();

        return name.Contains("sent")
            || name.Contains("inviata")
            || name.Contains("posta inviata");
    }

    private void TryAddGmailAllMail(ImapClient client, List<IMailFolder> list, string email)
    {
        var names = new[]
        {
        "[Gmail]/Tutti i messaggi",
        "[Gmail]/All Mail",
        "Tutti i messaggi",
        "All Mail"
    };

        foreach (var name in names)
        {
            try
            {
                var f = client.GetFolder(name);
                if (f != null)
                {
                    list.Add(f);
                    return;
                }
            }
            catch { }
        }

        _logger.LogWarning("Cartella All Mail non trovata per {Email}", email);
    }
    private async Task ProcessSummariesAsync(
  IMailFolder folder,
  List<IMessageSummary> summaries,
  OracleConnection dbConn,
  int casellaId,
  string accountEmail,
  string provider,
  HashSet<string> seenMessageIds,
  CancellationToken ct,
  bool isSentFolder,
  bool isSpamFolder)
    {
        if (summaries == null || summaries.Count == 0) return;

        foreach (var s in summaries.OrderBy(x => x.UniqueId.Id))
        {
            if (IsDraftSummary(s))
            {
                _logger.LogDebug(
                    "SKIP bozza [{Acc}] {Folder} uid={Uid}",
                    accountEmail, folder.FullName, (long)s.UniqueId.Id);
                continue;
            }
            int? forcedExistingSentId = null;
            var envMid = NormalizeMessageId(s.Envelope?.MessageId);

            if (!string.IsNullOrWhiteSpace(envMid))
            {
                var memoryKey = $"{(isSentFolder ? "SENT" : "INBOX")}:{envMid}";

                if (!seenMessageIds.Add(memoryKey))
                {
                    _logger.LogDebug(
                        "SKIP duplicata in memoria [{Acc}] {Folder} uid={Uid} mid={Mid}",
                        accountEmail, folder.FullName, (long)s.UniqueId.Id, envMid);
                    continue;
                }

                var existsByEnvelope = isSentFolder
                    ? await GetExistingSentEmailIdByMessageId(dbConn, casellaId, envMid, ct)
                    : await GetExistingEmailIdByMessageId(dbConn, casellaId, envMid, ct);

                if (existsByEnvelope.HasValue)
                {
                    if (isSentFolder)
                        forcedExistingSentId = existsByEnvelope.Value;
                    else
                        continue;
                }
            }

            var full = await folder.GetMessageAsync(s.UniqueId, ct);

            var mid = NormalizeMessageId(full.MessageId);
           
            if (string.IsNullOrWhiteSpace(mid))
                mid = BuildStableFallbackMessageId(full);

            // aggiungo al set SOLO se non avevo già aggiunto envMid
            if (string.IsNullOrWhiteSpace(envMid))
            {
                var memoryKey2 = $"{(isSentFolder ? "SENT" : "INBOX")}:{mid}";

                if (!seenMessageIds.Add(memoryKey2))
                {
                    _logger.LogDebug(
                        "SKIP duplicata post-fetch in memoria [{Acc}] {Folder} uid={Uid} mid={Mid}",
                        accountEmail, folder.FullName, (long)s.UniqueId.Id, mid);
                    continue;
                }
            }
            var existingId = isSentFolder
                ? await GetExistingSentEmailIdByMessageId(dbConn, casellaId, mid, ct)
                : await GetExistingEmailIdByMessageId(dbConn, casellaId, mid, ct);
            if (!isSentFolder)
            {
                var existsAsSent = await GetExistingSentEmailIdByMessageId(dbConn, casellaId, mid, ct);
                if (existsAsSent.HasValue)
                {
                    _logger.LogDebug(
                        "SKIP copia AllMail già presente in EMAIL_INVIATE [{Acc}] {Folder} uid={Uid} mid={Mid}",
                        accountEmail, folder.FullName, (long)s.UniqueId.Id, mid);
                    continue;
                }
            }
            if (existingId.HasValue)
            {
                if (isSentFolder)
                {
                    forcedExistingSentId = existingId.Value;
                }
                else
                {
                    _logger.LogDebug(
                        "SKIP duplicata post-fetch già a DB [{Acc}] {Folder} uid={Uid} mid={Mid}",
                        accountEmail, folder.FullName, (long)s.UniqueId.Id, mid);
                    continue;
                }
            }

            var uid = (long)s.UniqueId.Id;
            var emailDateBase = s.InternalDate?.DateTime ?? full.Date.DateTime;
            DateTime emailDateRome;
            if (s.InternalDate.HasValue)
            {
                emailDateRome = TimeZoneInfo.ConvertTime(s.InternalDate.Value, RomeTz).DateTime;
            }
            else
            {
                var msgOffset = full.Date;
                emailDateRome = TimeZoneInfo.ConvertTime(msgOffset, RomeTz).DateTime;
            }

            int emailId;

            var providerThreadKey = s.GMailThreadId.HasValue
                 ? $"gmail:{s.GMailThreadId.Value}"
                 : null;

            if (isSentFolder)
            {
                emailId = forcedExistingSentId ?? await SaveSentEmail(
                    dbConn,
                    casellaId,
                    mid,
                    full,
                    ct,
                    folder.FullName,
                    uid,
                    emailDateRome,
                    providerThreadKey
                );
            }
            else
            {
                emailId = await SaveEmail(
                    dbConn,
                    casellaId,
                    mid,
                    full,
                    ct,
                    folder.FullName,
                    uid,
                    emailDateRome,
                    providerThreadKey
                );
                var isBlacklisted = await IsSenderBlacklistedAsync(dbConn, full.From?.ToString(), ct);

                if (isSpamFolder)
                {
                    await AddSenderToBlacklistAsync(dbConn, full.From?.ToString(), "SYSTEM-SPAM", emailId, ct);
                }
                else if (!isBlacklisted)
                {
                    await ApplyRulesAsync(dbConn, emailId, full, emailDateRome, accountEmail, ct);
                }
            }

            var body = s.Body;
            if (body == null)
            {
                var fetched = await folder.FetchAsync(new[] { s.UniqueId }, MessageSummaryItems.BodyStructure, ct);
                body = fetched.FirstOrDefault()?.Body;
            }

            if (isSentFolder)
            {
                var atts = await SaveSentAttachmentsMetadata(dbConn, emailId, body, full, ct);

                await DownloadAndStoreSentAttachmentsAsync(
                    folder,
                    s.UniqueId,
                    casellaId,
                    emailId,
                    atts,
                    body,
                    dbConn,
                    ct
                );
            }
            else
            {
                var atts = await SaveAttachmentsMetadata(dbConn, emailId, body, full, ct);

                await DownloadAndStoreAttachmentsAsync(
                    folder,
                    s.UniqueId,
                    casellaId,
                    emailId,
                    atts,
                    body,
                    dbConn,
                    ct
                );
            }

            _logger.LogInformation(
                "🆕 NUOVA id={Id} [{Acc}] {Folder} uid={Uid} mid={Mid} subj='{Subj}'",
                emailId, accountEmail, folder.FullName, uid, mid, full.Subject
            );
        }
    }


    private async Task<int> SaveEmail(
    OracleConnection conn,
    int casellaId,
    string messageId,
    MimeMessage message,
    CancellationToken ct,
    string folderPath,
    long? messageUid,
    DateTime? internalDateRome = null,
   string? providerThreadKey = null)
    {
        messageId = NormalizeMessageId(messageId);
        if (string.IsNullOrWhiteSpace(messageId))
            messageId = BuildStableFallbackMessageId(message);

        var resolvedThread = await ResolveThreadKeyAsync(
       conn,
       casellaId,
       messageId,
       message,
       providerThreadKey,
       ct
   );

        var threadKey = resolvedThread.ThreadKey;
        var gmailThreadId = resolvedThread.GmailThreadKey ?? providerThreadKey;
        var blacklist = await IsSenderBlacklistedAsync(conn, message.From?.ToString(), ct);
        const string sql = @"
INSERT INTO SGAPP.EMAIL_RICEVUTE
    (CASELLA_ID, MESSAGE_ID, DATA_RICEZIONE, MITTENTE, DESTINATARI, CC, CCN, OGGETTO,
     CORPO_HTML, CORPO_TESTO, APERTO, ELIMINATO, FOLDER_PATH, MESSAGE_UID,
     IN_REPLY_TO, REFERENCES_HDR, THREAD_KEY,GMAIL_THREAD_ID, BLACKLIST)
VALUES
    (:p_cid, :p_mid, :p_dt, :p_from, :p_to, :p_cc, :p_ccn, :p_subj,
     :p_html, :p_text, 'N', 'N', :p_fp, :p_uid,
     :p_inreply, :p_refs, :p_thread, :p_gmail_thread_id, :p_blacklist)
RETURNING ID INTO :p_id";

        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };

        cmd.Parameters.Add("p_cid", OracleDbType.Int32).Value = casellaId;
        cmd.Parameters.Add("p_mid", OracleDbType.Varchar2, 500).Value = messageId;
        cmd.Parameters.Add("p_dt", OracleDbType.Date).Value =
            (object?)(internalDateRome ?? TimeZoneInfo.ConvertTime(message.Date, RomeTz).DateTime)
            ?? DBNull.Value;
        cmd.Parameters.Add("p_from", OracleDbType.Varchar2, 500).Value = message.From?.ToString() ?? "";
        cmd.Parameters.Add("p_to", OracleDbType.Varchar2, 2000).Value = message.To?.ToString() ?? "";
        cmd.Parameters.Add("p_cc", OracleDbType.Varchar2, 2000).Value = message.Cc?.ToString() ?? "";
        cmd.Parameters.Add("p_ccn", OracleDbType.Varchar2, 2000).Value = message.Bcc?.ToString() ?? "";
        cmd.Parameters.Add("p_gmail_thread_id", OracleDbType.Varchar2, 128).Value =
     !string.IsNullOrWhiteSpace(gmailThreadId)
         ? gmailThreadId
         : (object)DBNull.Value;
        var emailRome = internalDateRome ?? TimeZoneInfo.ConvertTime(message.Date, RomeTz).DateTime;

        string subject = message.Subject?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(subject))
        {
            var from = message.From?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(from)) from = "Sconosciuto";
            subject = $"(Senza oggetto) da {from} - {emailRome:yyyy-MM-dd HH:mm}";
        }

        var fp = NormalizeFolderPath(folderPath);
        if (subject.Length > 1000) subject = subject[..1000];

        cmd.Parameters.Add("p_subj", OracleDbType.Varchar2, 1000).Value = subject;

        var effectiveBody = ExtractEffectiveBody(message);
        cmd.Parameters.Add("p_html", OracleDbType.Clob).Value =
    (object?)effectiveBody.Html ?? DBNull.Value;

        cmd.Parameters.Add("p_text", OracleDbType.Clob).Value =
            (object?)effectiveBody.Text ?? DBNull.Value;
        cmd.Parameters.Add("p_fp", OracleDbType.Varchar2, 512).Value = fp;
        cmd.Parameters.Add("p_uid", OracleDbType.Int64).Value = (object?)messageUid ?? DBNull.Value;

        cmd.Parameters.Add("p_inreply", OracleDbType.Varchar2, 500).Value =
            !string.IsNullOrWhiteSpace(message.InReplyTo)
                ? NormalizeMessageId(message.InReplyTo)
                : (object)DBNull.Value;

        cmd.Parameters.Add("p_refs", OracleDbType.Clob).Value =
            message.References != null && message.References.Any()
                ? string.Join(" ", message.References.Select(NormalizeMessageId))
                : (object)DBNull.Value;

        cmd.Parameters.Add("p_thread", OracleDbType.Varchar2, 500).Value =
            threadKey ?? (object)DBNull.Value;
        cmd.Parameters.Add("p_blacklist", OracleDbType.Char, 1).Value =
   blacklist ? "Y" : "N";
        var outId = new OracleParameter("p_id", OracleDbType.Int32)
        {
            Direction = ParameterDirection.Output
        };
        cmd.Parameters.Add(outId);

        try
        {
            await cmd.ExecuteNonQueryAsync(ct);

            if (outId.Value is Oracle.ManagedDataAccess.Types.OracleDecimal o)
                return o.ToInt32();

            return Convert.ToInt32(outId.Value?.ToString());
        }
        catch (OracleException ex) when (ex.Number == 1)
        {
            var existingId = await GetExistingEmailIdByMessageId(conn, casellaId, messageId, ct);
            if (existingId.HasValue)
                return existingId.Value;

            throw;
        }
    }
    private async Task TouchExistingEmail(
     OracleConnection conn,
     int emailId,
     string currentFolderPath,
     long uid,
     CancellationToken ct)
    {
        var fp = NormalizeFolderPath(currentFolderPath);

        const string sql = @"
UPDATE SGAPP.EMAIL_RICEVUTE
   SET LAST_EVENT_AT = SYSTIMESTAMP,
       FOLDER_PATH   = NVL(FOLDER_PATH, :p_fp),
       MESSAGE_UID   = NVL(MESSAGE_UID, :p_uid)
 WHERE ID = :p_id";

        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
        cmd.Parameters.Add("p_fp", OracleDbType.Varchar2, 512).Value = fp;
        cmd.Parameters.Add("p_uid", OracleDbType.Int64).Value = uid;
        cmd.Parameters.Add("p_id", OracleDbType.Int32).Value = emailId;
        await cmd.ExecuteNonQueryAsync(ct);
    }


    private static string BuildStableFallbackMessageId(MimeMessage m)
    {
        var from = (m.From?.ToString() ?? "").Trim().ToLowerInvariant();
        var to = (m.To?.ToString() ?? "").Trim().ToLowerInvariant();
        var subj = (m.Subject ?? "").Trim().ToLowerInvariant();
        var date = m.Date.UtcDateTime.ToString("O");

        var key = $"{from}|{to}|{subj}|{date}";

        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(key);
        var hash = Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();

        return $"fallback:{hash}";
    }
    private async Task<List<(int AllegatoId, string FileName, string Mime, string PartSpec)>> SaveAttachmentsMetadata(
    OracleConnection conn,
    int emailId,
    BodyPart? body,
    MimeMessage fullMessage,
    CancellationToken ct)
    {
        var res = new List<(int, string, string, string)>();
        var list = new List<(string FileName, string Mime, string PartSpec)>();

        // Prima provo dal BodyStructure IMAP
        if (body != null)
            CollectAttachmentParts(body, list);

        // FALLBACK: se non trova nulla, provo dal messaggio completo
        if (list.Count == 0 && fullMessage?.Attachments != null)
        {
            int fallbackIndex = 1;

            foreach (var att in fullMessage.Attachments)
            {
                if (att is MimePart mp)
                {
                    var fileName = string.IsNullOrWhiteSpace(mp.FileName) ? $"allegato_{fallbackIndex}" : mp.FileName;
                    var mime = NormalizeAttachmentMime(
                        mp.ContentType?.MimeType,
                        fileName
                    );
                    // partSpec fittizio per avere almeno la riga DB
                    list.Add((fileName, mime, $"fallback-{fallbackIndex}"));
                    fallbackIndex++;
                }
                else if (att is MessagePart)
                {
                    list.Add(($"allegato_{fallbackIndex}.eml", "message/rfc822", $"fallback-{fallbackIndex}"));
                    fallbackIndex++;
                }
            }
        }

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
                    var mmDb = r.IsDBNull(2) ? null : r.GetString(2);
                    var ps = r.IsDBNull(3) ? (a.PartSpec ?? "") : r.GetString(3);

                    var mm = NormalizeAttachmentMime(mmDb, fn);

                    if (!string.Equals(mmDb, mm, StringComparison.OrdinalIgnoreCase))
                    {
                        const string updMime = @"
                        UPDATE SGAPP.EMAIL_ALLEGATI
                           SET MIME_TYPE = :p_mime
                         WHERE ID = :p_id";

                        await using var upd = new OracleCommand(updMime, conn) { BindByName = true };
                        upd.Parameters.Add("p_mime", OracleDbType.Varchar2, 255).Value = mm;
                        upd.Parameters.Add("p_id", OracleDbType.Int32).Value = existingId;
                        await upd.ExecuteNonQueryAsync(ct);
                    }

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

            var outId = new OracleParameter("p_id", OracleDbType.Int32)
            {
                Direction = ParameterDirection.Output
            };
            cmd.Parameters.Add(outId);

            await cmd.ExecuteNonQueryAsync(ct);

            var id = (outId.Value is Oracle.ManagedDataAccess.Types.OracleDecimal od)
                ? od.ToInt32()
                : Convert.ToInt32(outId.Value);

            res.Add((id, a.FileName ?? "allegato", a.Mime ?? "application/octet-stream", a.PartSpec ?? ""));
        }

        return res;
    }

    private static void CollectAttachmentParts(
    BodyPart part,
    List<(string FileName, string Mime, string PartSpec)> acc)
    {
        if (part is BodyPartBasic basic)
        {
            var fileName = basic.FileName?.Trim();
            var mime = NormalizeAttachmentMime(
                basic.ContentType?.MimeType,
                fileName
            );
            var disp = basic.ContentDisposition?.Disposition?.Trim();

            var isInline = string.Equals(disp, "inline", StringComparison.OrdinalIgnoreCase);

            var isEml =
                mime.Equals("message/rfc822", StringComparison.OrdinalIgnoreCase) ||
                fileName?.EndsWith(".eml", StringComparison.OrdinalIgnoreCase) == true;

            var isAttachment =
                isEml ||
                !string.IsNullOrWhiteSpace(fileName) ||
                string.Equals(disp, "attachment", StringComparison.OrdinalIgnoreCase) ||
                (!isInline && !mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase));

            if (isAttachment && !string.IsNullOrWhiteSpace(basic.PartSpecifier))
            {
                acc.Add((
                    string.IsNullOrWhiteSpace(fileName)
                        ? isEml ? "email_allegata.eml" : "allegato"
                        : fileName,
                    mime,
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

    private static string NormalizeAttachmentMime(
    string? mimeType,
    string? fileName)
    {
        var mime = (mimeType ?? "").Trim();
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();

        var byExt = ext switch
        {
            ".pdf" => "application/pdf",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".txt" => "text/plain",
            ".html" or ".htm" => "text/html",
            ".eml" => "message/rfc822",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xls" => "application/vnd.ms-excel",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            _ => null
        };

        if (!string.IsNullOrWhiteSpace(byExt))
            return byExt;

        if (!string.IsNullOrWhiteSpace(mime) &&
            !mime.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            return mime;
        }

        return ext switch
        {
            ".pdf" => "application/pdf",

            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",

            ".txt" => "text/plain",
            ".html" or ".htm" => "text/html",

            ".eml" => "message/rfc822",

            ".doc" => "application/msword",

            ".docx" =>
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",

            ".xls" => "application/vnd.ms-excel",

            ".xlsx" =>
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",

            _ => string.IsNullOrWhiteSpace(mime)
                ? "application/octet-stream"
                : mime
        };
    }

    private async Task<int> SaveEmbeddedEmailAsync(
    OracleConnection conn,
    int casellaId,
    int parentEmailId,
    int parentAllegatoId,
    MimeMessage embedded,
    CancellationToken ct)
    {
        var messageId = NormalizeMessageId(embedded.MessageId);

        if (string.IsNullOrWhiteSpace(messageId))
            messageId = BuildStableFallbackMessageId(embedded);

        var existing = await GetExistingEmailIdByMessageId(conn, casellaId, messageId, ct);
        if (existing.HasValue)
            return existing.Value;

        var dataRicezione = TimeZoneInfo.ConvertTime(embedded.Date, RomeTz).DateTime;

        var subject = embedded.Subject?.Trim();
        if (string.IsNullOrWhiteSpace(subject))
            subject = $"(Email allegata) - {dataRicezione:yyyy-MM-dd HH:mm}";

        if (subject.Length > 1000)
            subject = subject[..1000];

        var threadKey = messageId;

        const string sql = @"
INSERT INTO SGAPP.EMAIL_RICEVUTE
    (CASELLA_ID, MESSAGE_ID, DATA_RICEZIONE, MITTENTE, DESTINATARI, CC, CCN,
     OGGETTO, CORPO_HTML, CORPO_TESTO, APERTO, ELIMINATO,
     FOLDER_PATH, MESSAGE_UID, IN_REPLY_TO, REFERENCES_HDR, THREAD_KEY,
     BLACKLIST, IS_EML_IMPORTATA, PARENT_EMAIL_ID, PARENT_ALLEGATO_ID)
VALUES
    (:p_cid, :p_mid, :p_dt, :p_from, :p_to, :p_cc, :p_ccn,
     :p_subj, :p_html, :p_text, 'N', 'N',
     :p_fp, NULL, :p_inreply, :p_refs, :p_thread,
     'N', 'Y', :p_parent_email_id, :p_parent_allegato_id)
RETURNING ID INTO :p_id";

        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };

        cmd.Parameters.Add("p_cid", OracleDbType.Int32).Value = casellaId;
        cmd.Parameters.Add("p_mid", OracleDbType.Varchar2, 500).Value = messageId;
        cmd.Parameters.Add("p_dt", OracleDbType.Date).Value = dataRicezione;
        cmd.Parameters.Add("p_from", OracleDbType.Varchar2, 500).Value = embedded.From?.ToString() ?? "";
        cmd.Parameters.Add("p_to", OracleDbType.Varchar2, 2000).Value = embedded.To?.ToString() ?? "";
        cmd.Parameters.Add("p_cc", OracleDbType.Varchar2, 2000).Value = embedded.Cc?.ToString() ?? "";
        cmd.Parameters.Add("p_ccn", OracleDbType.Varchar2, 2000).Value = embedded.Bcc?.ToString() ?? "";
        cmd.Parameters.Add("p_subj", OracleDbType.Varchar2, 1000).Value = subject;
        string? htmlBody = embedded.HtmlBody;
        string? textBody = embedded.TextBody;

        // fallback solo se entrambi null
        if (string.IsNullOrWhiteSpace(htmlBody) &&
            string.IsNullOrWhiteSpace(textBody))
        {
            textBody = embedded.Body?.ToString();
        }

        cmd.Parameters.Add("p_html", OracleDbType.Clob).Value =
            (object?)htmlBody ?? DBNull.Value;

        cmd.Parameters.Add("p_text", OracleDbType.Clob).Value =
            (object?)textBody ?? DBNull.Value;

        cmd.Parameters.Add("p_fp", OracleDbType.Varchar2, 512).Value =
            $"EML_ATTACHMENT:{parentEmailId}:{parentAllegatoId}";

        cmd.Parameters.Add("p_inreply", OracleDbType.Varchar2, 500).Value =
            !string.IsNullOrWhiteSpace(embedded.InReplyTo)
                ? NormalizeMessageId(embedded.InReplyTo)
                : DBNull.Value;

        cmd.Parameters.Add("p_refs", OracleDbType.Clob).Value =
            embedded.References != null && embedded.References.Any()
                ? string.Join(" ", embedded.References.Select(NormalizeMessageId))
                : DBNull.Value;

        cmd.Parameters.Add("p_thread", OracleDbType.Varchar2, 500).Value = threadKey;

        cmd.Parameters.Add("p_parent_email_id", OracleDbType.Int32).Value = parentEmailId;
        cmd.Parameters.Add("p_parent_allegato_id", OracleDbType.Int32).Value = parentAllegatoId;

        var outId = new OracleParameter("p_id", OracleDbType.Int32)
        {
            Direction = ParameterDirection.Output
        };

        cmd.Parameters.Add(outId);

        await cmd.ExecuteNonQueryAsync(ct);

        if (outId.Value is Oracle.ManagedDataAccess.Types.OracleDecimal od)
            return od.ToInt32();

        return Convert.ToInt32(outId.Value?.ToString());
    }

    private static async Task MarkAttachmentAsEmailAsync(
    OracleConnection conn,
    int allegatoId,
    int embeddedEmailId,
    CancellationToken ct)
    {
        const string sql = @"
        UPDATE SGAPP.EMAIL_ALLEGATI
           SET IS_EMAIL_EML = 'Y',
               EMAIL_EML_ID = :p_email_id
         WHERE ID = :p_id";

        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };

        cmd.Parameters.Add("p_email_id", OracleDbType.Int32).Value = embeddedEmailId;
        cmd.Parameters.Add("p_id", OracleDbType.Int32).Value = allegatoId;

        await cmd.ExecuteNonQueryAsync(ct);
    }
    private static bool IsDraftSummary(IMessageSummary s)
    {
        if (s.Flags.HasValue && (s.Flags.Value & MessageFlags.Draft) != 0)
            return true;

        if (s.GMailLabels == null)
            return false;

        return s.GMailLabels.Any(l =>
            l.Equals(@"\Draft", StringComparison.OrdinalIgnoreCase) ||
            l.Equals("Draft", StringComparison.OrdinalIgnoreCase) ||
            l.Equals("Drafts", StringComparison.OrdinalIgnoreCase) ||
            l.Equals("Bozze", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("draft", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("bozz", StringComparison.OrdinalIgnoreCase));
    }
    private async Task ApplyRulesAsync(
    OracleConnection conn,
    int emailId,
    MimeMessage message,
    DateTime emailDateRome,
    string currentMailbox,
    CancellationToken ct)
    {
        const string sql = @"
        SELECT ID, MITTENTE_LIKE, DEST_LIKE, OGGETTO_LIKE, ASSEGNA_A, SOLO_INVIO
          FROM SGAPP.EMAIL_REGOLE
         WHERE ATTIVA = 'Y'
        AND CREATED_AT <= :p_mail_dt";

        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
        cmd.Parameters.Add("p_mail_dt", OracleDbType.Date).Value = emailDateRome;
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetInt32(0);
            var mittLike = reader.IsDBNull(1) ? null : reader.GetString(1);
            var destLike = reader.IsDBNull(2) ? null : reader.GetString(2);
            var oggLike = reader.IsDBNull(3) ? null : reader.GetString(3);
            var utenti = reader.IsDBNull(4) ? null : reader.GetString(4);
            var soloInvio = reader.GetString(5) == "Y";

            if (CheckRuleMatch(message, mittLike, destLike, oggLike, currentMailbox))
            {
                await AssignEmail(conn, emailId, utenti, soloInvio, ct);
                _logger.LogInformation("📥 Applicata regola {Id} per email '{Subj}'", id, message.Subject);
            }
        }
    }

    private async Task<int?> GetExistingSentEmailIdByMessageId(
    OracleConnection conn,
    int casellaId,
    string messageId,
    CancellationToken ct)
    {
        messageId = NormalizeMessageId(messageId);
        if (string.IsNullOrWhiteSpace(messageId))
            return null;

        const string sql = @"
SELECT ID
  FROM SGAPP.EMAIL_INVIATE
 WHERE CASELLA_ID = :p_cid
   AND LOWER(MESSAGE_ID) = :p_mid
 FETCH FIRST 1 ROWS ONLY";

        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
        cmd.Parameters.Add("p_cid", OracleDbType.Int32).Value = casellaId;
        cmd.Parameters.Add("p_mid", OracleDbType.Varchar2, 500).Value = messageId.ToLowerInvariant();

        var obj = await cmd.ExecuteScalarAsync(ct);
        if (obj == null || obj == DBNull.Value)
            return null;

        if (obj is Oracle.ManagedDataAccess.Types.OracleDecimal od)
            return od.ToInt32();

        return Convert.ToInt32(obj);
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

                // fallback: allegato letto dal messaggio completo
                if (!string.IsNullOrWhiteSpace(a.PartSpec) &&
                    a.PartSpec.StartsWith("fallback-", StringComparison.OrdinalIgnoreCase))
                {
                    var full = await folder.GetMessageAsync(uid, ct);
                    var allAtts = full.Attachments.ToList();

                    var suffix = a.PartSpec.Substring("fallback-".Length);

                    if (int.TryParse(suffix, out var fallbackIndex))
                    {
                        var idx = fallbackIndex - 1;

                        if (idx >= 0 && idx < allAtts.Count)
                        {
                            var fallbackEntity = allAtts[idx];

                            long fallbackSize;
                            await using (var fs = new FileStream(
                                        filePath,
                                        FileMode.Create,
                                        FileAccess.ReadWrite,
                                        FileShare.None,
                                        81920,
                                        useAsync: true))
                            {
                                if (fallbackEntity is MimePart fallbackMp)
                                {
                                    await fallbackMp.Content.DecodeToAsync(fs, ct);
                                }
                                else if (fallbackEntity is MessagePart fallbackMsgPart)
                                {
                                    await fallbackMsgPart.Message.WriteToAsync(fs, ct);
                                }
                                else
                                {
                                    await fallbackEntity.WriteToAsync(fs, ct);
                                }

                                await fs.FlushAsync(ct);

                                await TryImportEmlAttachmentAsync(
                                    conn,
                                    casellaId,
                                    emailId,
                                    a.AllegatoId,
                                    a.FileName,
                                    a.Mime,
                                    fallbackEntity,
                                    fs,
                                    ct
                                );

                                fallbackSize = fs.Length;
                            }

                            await UpdateAttachmentPathAsync(conn, a.AllegatoId, relPath, fallbackSize, false, ct);
                            continue;
                        }
                    }

                    _logger.LogWarning(
                        "Fallback attachment non trovato: emailId={EmailId} allegatoId={AllegatoId} partSpec='{PartSpec}'",
                        emailId, a.AllegatoId, a.PartSpec);
                    continue;
                }

                var bp = FindBodyPartBySpecifier(bodyStructure, a.PartSpec);
                if (bp == null)
                {
                    _logger.LogWarning(
                        "BodyPart non trovato: emailId={EmailId} allegatoId={AllegatoId} partSpec='{PartSpec}'",
                        emailId, a.AllegatoId, a.PartSpec ?? "(null)");
                    continue;
                }

                var imapEntity = await folder.GetBodyPartAsync(uid, bp, ct);
                if (imapEntity == null)
                {
                    _logger.LogWarning(
                        "GetBodyPartAsync ha restituito NULL: emailId={EmailId} allegatoId={AllegatoId} partSpec='{PartSpec}'",
                        emailId, a.AllegatoId, a.PartSpec ?? "(null)");
                    continue;
                }

                long size;
                await using (var fs = new FileStream(
                    filePath,
                    FileMode.Create,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    81920,
                    useAsync: true))
                {
                    if (imapEntity is MimePart mp)
                    {
                        await mp.Content.DecodeToAsync(fs, ct);
                    }
                    else if (imapEntity is MessagePart msgPart)
                    {
                        await msgPart.Message.WriteToAsync(fs, ct);
                    }
                    else
                    {
                        await imapEntity.WriteToAsync(fs, ct);
                    }

                    await fs.FlushAsync(ct);

                    await TryImportEmlAttachmentAsync(
                        conn,
                        casellaId,
                        emailId,
                        a.AllegatoId,
                        a.FileName,
                        a.Mime,
                        imapEntity,
                        fs,
                        ct
                    );

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
                try
                {
                    if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
                        File.Delete(filePath);
                }
                catch { }

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

    private bool CheckRuleMatch(MimeMessage msg,string? mitt,string? dest,string? subj,string? currentMailbox) {
        var fromText = msg.From?.ToString() ?? "";
        var toText = msg.To?.ToString() ?? "";
        var ccText = msg.Cc?.ToString() ?? "";
        var bccText = msg.Bcc?.ToString() ?? "";
        var subject = msg.Subject ?? "";

        var allRecipients =
            toText + " " +
            ccText + " " +
            bccText + " " +
            (currentMailbox ?? "");

        bool mittOk =
            string.IsNullOrWhiteSpace(mitt) ||
            mitt.Equals("TUTTI", StringComparison.OrdinalIgnoreCase) ||
            fromText.Contains(mitt, StringComparison.OrdinalIgnoreCase);

        bool destOk =
            string.IsNullOrWhiteSpace(dest) ||
            dest.Equals("TUTTI", StringComparison.OrdinalIgnoreCase) ||
            allRecipients.Contains(dest, StringComparison.OrdinalIgnoreCase);

        bool subjOk =
            string.IsNullOrWhiteSpace(subj) ||
            subj.Equals("TUTTI", StringComparison.OrdinalIgnoreCase) ||
            subject.Contains(subj, StringComparison.OrdinalIgnoreCase);

        return mittOk && destOk && subjOk;
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
        const string sql = @"
            SELECT EMAIL, PASSWORD, IMAP_HOST, IMAP_PORT, USE_SSL
            FROM SGAPP.CASELLEPOSTA
            WHERE ID = :p_id
              AND NVL(ATTIVA, 'Y') = 'Y'";

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
        var missingSent = await LoadMissingSentAttachmentsAsync(conn, limit, ct);
        if (missing.Count == 0 && missingSent.Count == 0)
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
        foreach (var byMailbox in missingSent.GroupBy(x => x.CasellaId))
        {
            var casellaId = byMailbox.Key;

            var acc = await LoadMailboxAsync(conn, casellaId, ct);
            if (acc == null)
                continue;

            using var proto = new ProtocolLogger(Stream.Null);
            using var client = new ImapClient(proto);

            client.AuthenticationMechanisms.Remove("XOAUTH2");
            var socket = acc.Value.UseSsl
                ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTlsWhenAvailable;

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
                            continue;

                        var uid = new UniqueId((uint)a.MessageUid);

                        await DownloadSingleSentAttachmentBackfillAsync(
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
                _logger.LogError(ex, "📎 Backfill inviati: errore su casellaId={Id}", casellaId);
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
            await using (var fs = new FileStream(
                         filePath,
                         FileMode.CreateNew,
                         FileAccess.ReadWrite,
                         FileShare.None,
                         81920,
                         useAsync: true))
            {
                if (entity is MimePart mp)
                    await mp.Content.DecodeToAsync(fs, ct);
                else if (entity is MessagePart msgPart)
                    await msgPart.Message.WriteToAsync(fs, ct);
                else
                    await entity.WriteToAsync(fs, ct);

                await fs.FlushAsync(ct);

                await TryImportEmlAttachmentAsync(
                    conn,
                    casellaId,
                    emailId,
                    allegatoId,
                    fileName,
                    mime,
                    entity,
                    fs,
                    ct
                );

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

        var items = MessageSummaryItems.UniqueId |
            MessageSummaryItems.Envelope |
            MessageSummaryItems.InternalDate |
            MessageSummaryItems.BodyStructure |
            MessageSummaryItems.Flags |
            MessageSummaryItems.GMailThreadId | MessageSummaryItems.GMailLabels; 
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
        messageId = NormalizeMessageId(messageId);
        if (string.IsNullOrWhiteSpace(messageId))
            return null;

        const string sql = @"
SELECT ID
  FROM SGAPP.EMAIL_RICEVUTE
 WHERE CASELLA_ID = :p_cid
   AND LOWER(MESSAGE_ID) = :p_mid
 FETCH FIRST 1 ROWS ONLY";

        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
        cmd.Parameters.Add("p_cid", OracleDbType.Int32).Value = casellaId;
        cmd.Parameters.Add("p_mid", OracleDbType.Varchar2, 500).Value = messageId.ToLowerInvariant();

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

        var items = MessageSummaryItems.UniqueId |
            MessageSummaryItems.Envelope |
            MessageSummaryItems.InternalDate |
            MessageSummaryItems.BodyStructure |
            MessageSummaryItems.GMailThreadId |
                MessageSummaryItems.Flags |
                MessageSummaryItems.GMailLabels;
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
    CancellationToken ct,
    bool isSentFolder)
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

        var items = MessageSummaryItems.UniqueId |
            MessageSummaryItems.Envelope |
            MessageSummaryItems.InternalDate |
            MessageSummaryItems.BodyStructure |
            MessageSummaryItems.GMailThreadId |
             MessageSummaryItems.Flags |
             MessageSummaryItems.GMailLabels;

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

            var exists = isSentFolder
                ? await GetExistingSentEmailIdByMessageId(conn, casellaId, mid, ct)
                : await GetExistingEmailIdByMessageId(conn, casellaId, mid, ct); if (!exists.HasValue)
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
        await using (var fs = new FileStream(
    filePath,
    FileMode.CreateNew,
    FileAccess.ReadWrite,
    FileShare.None,
    81920,
    useAsync: true))
        {
            if (entity is MimePart mp)
                await mp.Content.DecodeToAsync(fs, ct);
            else if (entity is MessagePart msgPart)
                await msgPart.Message.WriteToAsync(fs, ct);
            else
                await entity.WriteToAsync(fs, ct);

            await fs.FlushAsync(ct);

            await TryImportEmlAttachmentAsync(
                conn,
                info.CasellaId,
                info.EmailId,
                info.AllegatoId,
                info.FileName,
                info.Mime,
                entity,
                fs,
                ct
            );

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

    private static string ExtractEmailAddress(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        raw = raw.Trim();

        var match = System.Text.RegularExpressions.Regex.Match(
            raw,
            @"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}"
        );

        return match.Success ? match.Value.Trim().ToLowerInvariant() : raw.ToLowerInvariant();
    }

    private async Task<bool> IsSenderBlacklistedAsync(
        OracleConnection conn,
        string? sender,
        CancellationToken ct)
    {
        var email = ExtractEmailAddress(sender);

        if (string.IsNullOrWhiteSpace(email))
            return false;

        const string sql = @"
SELECT COUNT(*)
FROM SGAPP.EMAIL_BLACKLIST
WHERE LOWER(EMAIL) = :p_email
  AND NVL(ATTIVA,'Y') = 'Y'";

        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
        cmd.Parameters.Add("p_email", OracleDbType.Varchar2, 500).Value = email;

        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result) > 0;
    }

    private static bool IsSpamFolder(IMailFolder folder)
    {
        var name = NormalizeFolderPath(folder.FullName).ToLowerInvariant();

        return name.Contains("spam")
            || name.Contains("junk")
            || name.Contains("posta indesiderata")
            || name.Contains("indesiderata");
    }

    private async Task<int> SaveSentEmail(
    OracleConnection conn,
    int casellaId,
    string messageId,
    MimeMessage message,
    CancellationToken ct,
    string folderPath,
    long? messageUid,
    DateTime? internalDateRome = null,
    string? providerThreadKey = null)
    {
        messageId = NormalizeMessageId(messageId);
        if (string.IsNullOrWhiteSpace(messageId))
            messageId = BuildStableFallbackMessageId(message);

        var emailRome = internalDateRome ?? TimeZoneInfo.ConvertTime(message.Date, RomeTz).DateTime;

        var subject = message.Subject?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(subject))
            subject = $"(Senza oggetto) - {emailRome:yyyy-MM-dd HH:mm}";

        if (subject.Length > 1000)
            subject = subject[..1000];

        var resolvedThread = await ResolveThreadKeyAsync(
     conn,
     casellaId,
     messageId,
     message,
     providerThreadKey,
     ct
 );

        var threadKey = resolvedThread.ThreadKey;
        var gmailThreadId = resolvedThread.GmailThreadKey ?? providerThreadKey;

        const string sql = @"
INSERT INTO SGAPP.EMAIL_INVIATE
    (CASELLA_ID, UTENTE, CORPO_TESTO, DATA_INVIO, CORPO_HTML,
     DESTINATARI, OGGETTO, MESSAGE_ID, IN_REPLY_TO, REFERENCES_HDR,
     THREAD_KEY, CC, BCC, FOLDER_PATH, MESSAGE_UID, GMAIL_THREAD_ID)
VALUES
    (:p_cid, :p_utente, :p_text, :p_dt, :p_html,
     :p_to, :p_subj, :p_mid, :p_inreply, :p_refs,
     :p_thread, :p_cc, :p_bcc, :p_fp, :p_uid, :p_gmail_thread_id)
RETURNING ID INTO :p_id";

        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };

        cmd.Parameters.Add("p_cid", OracleDbType.Int32).Value = casellaId;

        // per ora metto la mail come utente; se hai username reale, meglio passarlo
        cmd.Parameters.Add("p_utente", OracleDbType.Varchar2, 255).Value =
            ExtractEmailAddress(message.From?.ToString());

        cmd.Parameters.Add("p_text", OracleDbType.Clob).Value =
            (object?)message.TextBody ?? DBNull.Value;

        cmd.Parameters.Add("p_dt", OracleDbType.Date).Value = emailRome;

        cmd.Parameters.Add("p_html", OracleDbType.Clob).Value =
            (object?)message.HtmlBody ?? DBNull.Value;

        cmd.Parameters.Add("p_to", OracleDbType.Clob).Value =
            message.To?.ToString() ?? "";

        cmd.Parameters.Add("p_subj", OracleDbType.Clob).Value = subject;

        cmd.Parameters.Add("p_mid", OracleDbType.Varchar2, 500).Value = messageId;

        cmd.Parameters.Add("p_inreply", OracleDbType.Varchar2, 500).Value =
            !string.IsNullOrWhiteSpace(message.InReplyTo)
                ? NormalizeMessageId(message.InReplyTo)
                : DBNull.Value;

        cmd.Parameters.Add("p_refs", OracleDbType.Clob).Value =
            message.References != null && message.References.Any()
                ? string.Join(" ", message.References.Select(NormalizeMessageId))
                : DBNull.Value;

        cmd.Parameters.Add("p_thread", OracleDbType.Varchar2, 500).Value = threadKey;

        cmd.Parameters.Add("p_cc", OracleDbType.Varchar2, 4000).Value =
            message.Cc?.ToString() ?? "";

        cmd.Parameters.Add("p_bcc", OracleDbType.Varchar2, 4000).Value =
            message.Bcc?.ToString() ?? "";
        cmd.Parameters.Add("p_fp", OracleDbType.Varchar2, 512).Value = NormalizeFolderPath(folderPath);
        cmd.Parameters.Add("p_uid", OracleDbType.Int64).Value = (object?)messageUid ?? DBNull.Value;
        var outId = new OracleParameter("p_id", OracleDbType.Int32)
        {
            Direction = ParameterDirection.Output
        };
        cmd.Parameters.Add("p_gmail_thread_id", OracleDbType.Varchar2, 128).Value =
    !string.IsNullOrWhiteSpace(gmailThreadId)
        ? gmailThreadId
        : (object)DBNull.Value;
        cmd.Parameters.Add(outId);

        try
        {
            await cmd.ExecuteNonQueryAsync(ct);

            if (outId.Value is Oracle.ManagedDataAccess.Types.OracleDecimal od)
                return od.ToInt32();

            return Convert.ToInt32(outId.Value?.ToString());
        }
        catch (OracleException ex) when (ex.Number == 1)
        {
            var existing = await GetExistingSentEmailIdByMessageId(conn, casellaId, messageId, ct);
            if (existing.HasValue)
                return existing.Value;

            throw;
        }
    }

    private async Task<List<(int AllegatoId, string FileName, string Mime, string PartSpec)>> SaveSentAttachmentsMetadata(
    OracleConnection conn,
    int emailId,
    BodyPart? body,
    MimeMessage fullMessage,
    CancellationToken ct)
    {
        var res = new List<(int, string, string, string)>();
        var list = new List<(string FileName, string Mime, string PartSpec)>();

        if (body != null)
            CollectAttachmentParts(body, list);

        if (list.Count == 0 && fullMessage?.Attachments != null)
        {
            int fallbackIndex = 1;

            foreach (var att in fullMessage.Attachments)
            {
                if (att is MimePart mp)
                {
                    var fileName = string.IsNullOrWhiteSpace(mp.FileName)
                        ? $"allegato_{fallbackIndex}"
                        : mp.FileName;

                    var mime = NormalizeAttachmentMime(
                        mp.ContentType?.MimeType,
                        fileName
                    );
                    list.Add((fileName, mime, $"fallback-{fallbackIndex}"));
                    fallbackIndex++;
                }
                else if (att is MessagePart)
                {
                    list.Add(($"allegato_{fallbackIndex}.eml", "message/rfc822", $"fallback-{fallbackIndex}"));
                    fallbackIndex++;
                }
            }
        }

        foreach (var a in list)
        {
            const string existsSql = @"
SELECT ID, NOME_FILE, MIME_TYPE, PART_SPEC
  FROM SGAPP.INVIATA_ALLEGATI
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
                    var fn = r.IsDBNull(1) ? a.FileName : r.GetString(1);
                    var mm = r.IsDBNull(2) ? a.Mime : r.GetString(2);

                    res.Add((existingId, fn, mm, a.PartSpec));
                    continue;
                }
            }

            const string sql = @"
            INSERT INTO SGAPP.INVIATA_ALLEGATI
                (EMAIL_ID, NOME_FILE, MIME_TYPE, PART_SPEC, CONTENT)
            VALUES
                (:p_eid, :p_name, :p_mime, :p_part, EMPTY_BLOB())
            RETURNING ID INTO :p_id";

            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };

            cmd.Parameters.Add("p_eid", OracleDbType.Int32).Value = emailId;
            cmd.Parameters.Add("p_name", OracleDbType.Varchar2, 512).Value = a.FileName ?? "allegato";
            cmd.Parameters.Add("p_mime", OracleDbType.Varchar2, 255).Value = a.Mime ?? "application/octet-stream";
            cmd.Parameters.Add("p_part", OracleDbType.Varchar2, 64).Value = a.PartSpec ?? "";
            var outId = new OracleParameter("p_id", OracleDbType.Int32)
            {
                Direction = ParameterDirection.Output
            };

            cmd.Parameters.Add(outId);

            await cmd.ExecuteNonQueryAsync(ct);

            var id = outId.Value is Oracle.ManagedDataAccess.Types.OracleDecimal od
                ? od.ToInt32()
                : Convert.ToInt32(outId.Value?.ToString());

            res.Add((id, a.FileName ?? "allegato", a.Mime ?? "application/octet-stream", a.PartSpec ?? ""));
        }

        return res;
    }

    private async Task DownloadAndStoreSentAttachmentsAsync(
    IMailFolder folder,
    UniqueId uid,
    int casellaId,
    int emailId,
    List<(int AllegatoId, string FileName, string Mime, string PartSpec)> attachments,
    BodyPart? bodyStructure,
    OracleConnection conn,
    CancellationToken ct)
    {
        if (attachments == null || attachments.Count == 0)
            return;

        if (bodyStructure == null)
        {
            var sums = await folder.FetchAsync(new[] { uid }, MessageSummaryItems.BodyStructure, ct);
            bodyStructure = sums.FirstOrDefault()?.Body;
            if (bodyStructure == null)
                return;
        }

        var emailDir = Path.Combine(_attachmentsBasePath, "inviati", casellaId.ToString(), emailId.ToString());
        Directory.CreateDirectory(emailDir);

        foreach (var a in attachments)
        {
            string? filePath = null;

            try
            {
                var safeName = SanitizeFileName(string.IsNullOrWhiteSpace(a.FileName) ? "allegato" : a.FileName);

                filePath = Path.Combine(emailDir, $"{a.AllegatoId}_{safeName}");

                var relPath = Path.Combine(
                    "inviati",
                    casellaId.ToString(),
                    emailId.ToString(),
                    $"{a.AllegatoId}_{safeName}"
                );

                if (!string.IsNullOrWhiteSpace(a.PartSpec) &&
                    a.PartSpec.StartsWith("fallback-", StringComparison.OrdinalIgnoreCase))
                {
                    var full = await folder.GetMessageAsync(uid, ct);
                    var allAtts = full.Attachments.ToList();

                    var suffix = a.PartSpec.Substring("fallback-".Length);

                    if (int.TryParse(suffix, out var fallbackIndex))
                    {
                        var idx = fallbackIndex - 1;

                        if (idx >= 0 && idx < allAtts.Count)
                        {
                            var fallbackEntity = allAtts[idx];

                            long fallbackSize;

                            await using (var fs = new FileStream(
                                filePath,
                                FileMode.Create,
                                FileAccess.Write,
                                FileShare.None,
                                81920,
                                useAsync: true))
                            {
                                if (fallbackEntity is MimePart fallbackMp)
                                    await fallbackMp.Content.DecodeToAsync(fs, ct);
                                else if (fallbackEntity is MessagePart fallbackMsgPart)
                                    await fallbackMsgPart.Message.WriteToAsync(fs, ct);
                                else
                                    await fallbackEntity.WriteToAsync(fs, ct);

                                await fs.FlushAsync(ct);
                                fallbackSize = fs.Length;
                            }

                            await UpdateSentAttachmentPathAsync(conn, a.AllegatoId, relPath, fallbackSize, ct);
                            continue;
                        }
                    }

                    continue;
                }

                var bp = FindBodyPartBySpecifier(bodyStructure, a.PartSpec);
                if (bp == null)
                    continue;

                var imapEntity = await folder.GetBodyPartAsync(uid, bp, ct);
                if (imapEntity == null)
                    continue;

                long size;

                await using (var fs = new FileStream(
                    filePath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    useAsync: true))
                {
                    if (imapEntity is MimePart mp)
                        await mp.Content.DecodeToAsync(fs, ct);
                    else if (imapEntity is MessagePart msgPart)
                        await msgPart.Message.WriteToAsync(fs, ct);
                    else
                        await imapEntity.WriteToAsync(fs, ct);

                    await fs.FlushAsync(ct);
                    size = fs.Length;
                }

                await UpdateSentAttachmentPathAsync(conn, a.AllegatoId, relPath, size, ct);
            }
            catch (Exception ex)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
                        File.Delete(filePath);
                }
                catch { }

                _logger.LogError(ex, "Errore salvataggio allegato inviato ID={AllegatoId} email={EmailId}", a.AllegatoId, emailId);
            }
        }
    }

    private static async Task UpdateSentAttachmentPathAsync(
    OracleConnection conn,
    int allegatoId,
    string relPath,
    long size,
    CancellationToken ct)
    {
        const string sql = @"
UPDATE SGAPP.INVIATA_ALLEGATI
   SET PATH = :p_path,
       FILE_SIZE = :p_size
 WHERE ID = :p_id
   AND PATH IS NULL";

        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
        cmd.Parameters.Add("p_path", OracleDbType.Varchar2, 1024).Value = relPath;
        cmd.Parameters.Add("p_size", OracleDbType.Int64).Value = size;
        cmd.Parameters.Add("p_id", OracleDbType.Int32).Value = allegatoId;

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<List<(int AllegatoId, int EmailId, int CasellaId, string FolderPath, long MessageUid, string FileName, string Mime, string PartSpec)>>
LoadMissingSentAttachmentsAsync(OracleConnection conn, int limit, CancellationToken ct)
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
  FROM SGAPP.INVIATA_ALLEGATI a
  JOIN SGAPP.EMAIL_INVIATE e ON e.ID = a.EMAIL_ID
 WHERE a.PATH IS NULL
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

    private async Task DownloadSingleSentAttachmentBackfillAsync(
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

            var emailDir = Path.Combine(_attachmentsBasePath, "inviati", casellaId.ToString(), emailId.ToString());
            Directory.CreateDirectory(emailDir);

            filePath = Path.Combine(emailDir, $"{allegatoId}_{safeName}");

            var relPath = Path.Combine(
                "inviati",
                casellaId.ToString(),
                emailId.ToString(),
                $"{allegatoId}_{safeName}"
            );

            if (File.Exists(filePath))
            {
                var fi = new FileInfo(filePath);
                await UpdateSentAttachmentPathAsync(conn, allegatoId, relPath, fi.Length, ct);
                return;
            }

            // ✅ CASO FALLBACK: partSpec = fallback-1 / fallback-2 / ecc.
            if (!string.IsNullOrWhiteSpace(partSpec) &&
                partSpec.StartsWith("fallback-", StringComparison.OrdinalIgnoreCase))
            {
                var full = await folder.GetMessageAsync(uid, ct);
                var allAtts = full.Attachments.ToList();

                var suffix = partSpec.Substring("fallback-".Length);

                if (int.TryParse(suffix, out var fallbackIndex))
                {
                    var idx = fallbackIndex - 1;

                    if (idx >= 0 && idx < allAtts.Count)
                    {
                        var fallbackEntity = allAtts[idx];

                        long fallbackSize;

                        await using (var fs = new FileStream(
                            filePath,
                            FileMode.Create,
                            FileAccess.Write,
                            FileShare.None,
                            81920,
                            useAsync: true))
                        {
                            if (fallbackEntity is MimePart mp)
                                await mp.Content.DecodeToAsync(fs, ct);
                            else if (fallbackEntity is MessagePart msgPart)
                                await msgPart.Message.WriteToAsync(fs, ct);
                            else
                                await fallbackEntity.WriteToAsync(fs, ct);

                            await fs.FlushAsync(ct);
                            fallbackSize = fs.Length;
                        }

                        await UpdateSentAttachmentPathAsync(conn, allegatoId, relPath, fallbackSize, ct);
                    }
                }

                return;
            }

            // ✅ CASO NORMALE: partSpec reale tipo "2", "3.1", ecc.
            var summaries = await folder.FetchAsync(new[] { uid }, MessageSummaryItems.BodyStructure, ct);
            var body = summaries.FirstOrDefault()?.Body;
            if (body == null) return;

            var bp = FindBodyPartBySpecifier(body, partSpec);
            if (bp == null) return;

            var entity = await folder.GetBodyPartAsync(uid, bp, ct);
            if (entity == null) return;

            long size;

            await using (var fs = new FileStream(
                filePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true))
            {
                if (entity is MimePart mp)
                    await mp.Content.DecodeToAsync(fs, ct);
                else if (entity is MessagePart msgPart)
                    await msgPart.Message.WriteToAsync(fs, ct);
                else
                    await entity.WriteToAsync(fs, ct);

                await fs.FlushAsync(ct);
                size = fs.Length;
            }

            await UpdateSentAttachmentPathAsync(conn, allegatoId, relPath, size, ct);
        }
        catch
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
                    File.Delete(filePath);
            }
            catch { }

            throw;
        }
    }

    private async Task TryImportEmlAttachmentAsync(
    OracleConnection conn,
    int casellaId,
    int emailId,
    int allegatoId,
    string fileName,
    string mime,
    MimeEntity entity,
    Stream fs,
    CancellationToken ct)
    {
        // se questa email è già una .eml importata, non importare altri .eml dentro
        // così eviti il loop infinito
        if (await IsImportedEmlAsync(conn, emailId, ct))
            return;

        MimeMessage? embeddedMessage = null;

        if (entity is MessagePart msgPart)
        {
            embeddedMessage = msgPart.Message;
        }
        else
        {
            var isEml =
                mime.Equals("message/rfc822", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".eml", StringComparison.OrdinalIgnoreCase);

            if (!isEml)
                return;

            fs.Position = 0;
            embeddedMessage = await MimeMessage.LoadAsync(fs, ct);
        }

        if (embeddedMessage == null)
            return;

        // evita caso in cui l'eml interno sia uguale alla mail padre
        var parentMid = await GetEmailMessageIdAsync(conn, emailId, ct);
        var embeddedMid = NormalizeMessageId(embeddedMessage.MessageId);

        if (!string.IsNullOrWhiteSpace(parentMid) &&
            !string.IsNullOrWhiteSpace(embeddedMid) &&
            string.Equals(parentMid, embeddedMid, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var embeddedEmailId = await SaveEmbeddedEmailAsync(
            conn,
            casellaId,
            emailId,
            allegatoId,
            embeddedMessage,
            ct
        );

        var embeddedAttachments = await SaveAttachmentsMetadata(
            conn,
            embeddedEmailId,
            null,
            embeddedMessage,
            ct
        );

        await DownloadEmbeddedAttachmentsAsync(
            conn,
            casellaId,
            embeddedEmailId,
            embeddedAttachments,
            embeddedMessage,
            ct
        );

        await MarkAttachmentAsEmailAsync(conn, allegatoId, embeddedEmailId, ct);
    }

    private async Task AddSenderToBlacklistAsync(
    OracleConnection conn,
    string? sender,
    string utente,
    int emailId,
    CancellationToken ct)
    {
        var email = ExtractEmailAddress(sender);

        if (string.IsNullOrWhiteSpace(email))
            return;

        const string mergeSql = @"
MERGE INTO SGAPP.EMAIL_BLACKLIST t
USING (
    SELECT :p_email AS EMAIL,
           :p_utente AS INSERITO_DA
    FROM dual
) s
ON (LOWER(t.EMAIL) = LOWER(s.EMAIL) AND NVL(t.ATTIVA,'Y') = 'Y')
WHEN NOT MATCHED THEN
    INSERT (EMAIL, INSERITO_DA, DATA_INSERIMENTO, ATTIVA)
    VALUES (s.EMAIL, s.INSERITO_DA, SYSDATE, 'Y')";

        await using (var cmd = new OracleCommand(mergeSql, conn) { BindByName = true })
        {
            cmd.Parameters.Add("p_email", OracleDbType.Varchar2, 500).Value = email;
            cmd.Parameters.Add("p_utente", OracleDbType.Varchar2, 200).Value = utente;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        const string updateSql = @"
UPDATE SGAPP.EMAIL_RICEVUTE
   SET BLACKLIST = 'Y'
 WHERE ID = :p_id";

        await using (var cmd = new OracleCommand(updateSql, conn) { BindByName = true })
        {
            cmd.Parameters.Add("p_id", OracleDbType.Int32).Value = emailId;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task DownloadEmbeddedAttachmentsAsync(
    OracleConnection conn,
    int casellaId,
    int emailId,
    List<(int AllegatoId, string FileName, string Mime, string PartSpec)> attachments,
    MimeMessage embeddedMessage,
    CancellationToken ct)
    {
        if (attachments == null || attachments.Count == 0)
            return;

        var emailDir = Path.Combine(
            _attachmentsBasePath,
            casellaId.ToString(),
            emailId.ToString()
        );

        Directory.CreateDirectory(emailDir);

        var allAttachments = embeddedMessage.Attachments.ToList();

        for (int i = 0; i < attachments.Count; i++)
        {
            var a = attachments[i];

            if (i >= allAttachments.Count)
                continue;

            var entity = allAttachments[i];

            var safeName = SanitizeFileName(
                string.IsNullOrWhiteSpace(a.FileName)
                    ? "allegato"
                    : a.FileName
            );

            var filePath = Path.Combine(
                emailDir,
                $"{a.AllegatoId}_{safeName}"
            );

            var relPath = Path.Combine(
                casellaId.ToString(),
                emailId.ToString(),
                $"{a.AllegatoId}_{safeName}"
            );

            long size;

            await using (var fs = new FileStream(
                filePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true))
            {
                if (entity is MimePart mp)
                {
                    await mp.Content.DecodeToAsync(fs, ct);
                }
                else if (entity is MessagePart msgPart)
                {
                    await msgPart.Message.WriteToAsync(fs, ct);
                }
                else
                {
                    await entity.WriteToAsync(fs, ct);
                }

                await fs.FlushAsync(ct);

                size = fs.Length;
            }

            await UpdateAttachmentPathAsync(
                conn,
                a.AllegatoId,
                relPath,
                size,
                true,
                ct
            );

            var isNestedEml =
     string.Equals(a.Mime, "message/rfc822", StringComparison.OrdinalIgnoreCase) ||
     (a.FileName?.EndsWith(".eml", StringComparison.OrdinalIgnoreCase) == true) ||
     entity is MessagePart;
            if (isNestedEml)
            {
                try
                {
                    await using var readFs = File.OpenRead(filePath);

                    await TryImportEmlAttachmentAsync(
                        conn,
                        casellaId,
                        emailId,
                        a.AllegatoId,
                        a.FileName,
                        a.Mime,
                        entity,
                        readFs,
                        ct
                    );
                }
                catch
                {
                    // ignore nested parse errors
                }
            }
        }
    }

    private static (string? Html, string? Text) ExtractEffectiveBody(MimeMessage message)
    {
        var html = message.HtmlBody;
        var text = message.TextBody;

        // HTML
        if (!string.IsNullOrWhiteSpace(html))
        {
            html = CleanPecWrapper(html);

            if (!string.IsNullOrWhiteSpace(html))
                return (html, null);
        }

        // TESTO
        if (!string.IsNullOrWhiteSpace(text))
        {
            text = CleanPecWrapper(text);

            if (!string.IsNullOrWhiteSpace(text))
                return (null, text);
        }

        return (html, text);
    }

    private static string CleanPecWrapper(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return body;

        var markers = new[]
        {
        "Messaggio di posta certificata",
        "Certified mail message"
    };

        foreach (var marker in markers)
        {
            var idx = body.IndexOf(marker, StringComparison.OrdinalIgnoreCase);

            if (idx >= 0)
            {
                // cerca il vero contenuto dopo il wrapper PEC
                var fromIdx = body.IndexOf("From:", idx, StringComparison.OrdinalIgnoreCase);

                if (fromIdx < 0)
                    fromIdx = body.IndexOf("Da:", idx, StringComparison.OrdinalIgnoreCase);

                if (fromIdx >= 0)
                {
                    return body.Substring(fromIdx).Trim();
                }
            }
        }

        return body;
    }
    private static bool IsDraftFolder(IMailFolder folder)
    {
        var name = NormalizeFolderPath(folder.FullName).ToLowerInvariant();

        return name.Contains("draft")
            || name.Contains("bozze")
            || name.Contains("bozza")
            || name.Contains("[gmail]/drafts")
            || name.Contains("[gmail]/bozze");
    }

    private static bool IsPecWrapperBody(string? html, string? text)
    {
        var body = ((html ?? "") + " " + (text ?? "")).Trim();

        if (string.IsNullOrWhiteSpace(body))
            return false;

        return body.Contains("Messaggio di posta certificata", StringComparison.OrdinalIgnoreCase)
            || body.Contains("Certified mail message", StringComparison.OrdinalIgnoreCase)
            || body.Contains("Il messaggio originale è incluso in allegato", StringComparison.OrdinalIgnoreCase)
            || body.Contains("The original message is included as an attachment", StringComparison.OrdinalIgnoreCase)
            || body.Contains("postacert.eml", StringComparison.OrdinalIgnoreCase)
            || body.Contains("daticert.xml", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> IsImportedEmlAsync(
    OracleConnection conn,
    int emailId,
    CancellationToken ct)
    {
        const string sql = @"
SELECT NVL(IS_EML_IMPORTATA, 'N')
FROM SGAPP.EMAIL_RICEVUTE
WHERE ID = :p_id";

        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
        cmd.Parameters.Add("p_id", OracleDbType.Int32).Value = emailId;

        var obj = await cmd.ExecuteScalarAsync(ct);

        return obj != null &&
               obj != DBNull.Value &&
               obj.ToString() == "Y";
    }

    private static async Task<string?> GetEmailMessageIdAsync(
        OracleConnection conn,
        int emailId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT LOWER(MESSAGE_ID)
FROM SGAPP.EMAIL_RICEVUTE
WHERE ID = :p_id";

        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
        cmd.Parameters.Add("p_id", OracleDbType.Int32).Value = emailId;

        var obj = await cmd.ExecuteScalarAsync(ct);
        return obj == null || obj == DBNull.Value ? null : obj.ToString();
    }
}
