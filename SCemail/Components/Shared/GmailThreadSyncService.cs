using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using MimeKit;
using Oracle.ManagedDataAccess.Client;
using System.Data;

public class GmailThreadSyncService
{
    private readonly IConfiguration _config;
    private readonly ILogger<GmailThreadSyncService> _logger;
    private readonly string _attachmentsBasePath;

    private static readonly string[] GmailScopes =
    {
        GmailService.Scope.GmailReadonly
    };

    private static readonly TimeZoneInfo RomeTz =
        TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "W. Europe Standard Time" : "Europe/Rome"
        );

    public GmailThreadSyncService(
        IConfiguration config,
        ILogger<GmailThreadSyncService> logger)
    {
        _config = config;
        _logger = logger;
        _attachmentsBasePath = _config.GetValue<string>("Attachments:BasePath")
                               ?? Path.Combine(AppContext.BaseDirectory, "attachments");

        Directory.CreateDirectory(_attachmentsBasePath);
    }

    public async Task SyncThreadByRfcMessageIdAsync(
        int casellaId,
        string mailboxEmail,
        string rfcMessageId,
        CancellationToken ct)
    {
        if (!_config.GetValue<bool?>("GmailApi:Enabled").GetValueOrDefault())
            return;

        rfcMessageId = NormalizeMessageId(rfcMessageId) ?? "";

        if (string.IsNullOrWhiteSpace(rfcMessageId))
            return;

        var gmail = CreateGmailService(mailboxEmail);

        var query = $"rfc822msgid:<{rfcMessageId}>";

        var listReq = gmail.Users.Messages.List("me");
        listReq.Q = query;
        listReq.MaxResults = 10;

        var list = await listReq.ExecuteAsync(ct);

        var first = list.Messages?.FirstOrDefault();

        if (first == null)
        {
            _logger.LogWarning(
                "Gmail API: nessun messaggio trovato per casella={Mailbox}, rfc822msgid={MessageId}",
                mailboxEmail,
                rfcMessageId);
            return;
        }

        var msgReq = gmail.Users.Messages.Get("me", first.Id);
        msgReq.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Metadata;
        var msg = await msgReq.ExecuteAsync(ct);

        if (string.IsNullOrWhiteSpace(msg.ThreadId))
            return;

        await SyncThreadAsync(casellaId, mailboxEmail, msg.ThreadId, ct);
    }

    public async Task SyncThreadAsync(
        int casellaId,
        string mailboxEmail,
        string gmailThreadId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(gmailThreadId))
            return;

        var connString = _config.GetConnectionString("OracleDb")
                         ?? throw new InvalidOperationException("ConnectionString OracleDb mancante.");

        var gmail = CreateGmailService(mailboxEmail);

        var threadReq = gmail.Users.Threads.Get("me", gmailThreadId);
        threadReq.Format = UsersResource.ThreadsResource.GetRequest.FormatEnum.Metadata;

        var thread = await threadReq.ExecuteAsync(ct);

        if (thread.Messages == null || thread.Messages.Count == 0)
            return;

        await using var conn = new OracleConnection(connString);
        await conn.OpenAsync(ct);

        foreach (var threadMsg in thread.Messages.OrderBy(x => ParseInternalDate(x.InternalDate)))
        {
            if (string.IsNullOrWhiteSpace(threadMsg.Id))
                continue;

            if (await GmailMessageAlreadyExistsAsync(conn, threadMsg.Id, ct))
            {
                await EnsureGmailThreadKeyAsync(conn, threadMsg.Id, gmailThreadId, ct);
                continue;
            }

            var rawReq = gmail.Users.Messages.Get("me", threadMsg.Id);
            rawReq.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Raw;

            var rawMsg = await rawReq.ExecuteAsync(ct);

            if (string.IsNullOrWhiteSpace(rawMsg.Raw))
                continue;

            var bytes = DecodeBase64Url(rawMsg.Raw);

            MimeMessage mime;
            await using (var ms = new MemoryStream(bytes))
            {
                mime = await MimeMessage.LoadAsync(ms, ct);
            }

            var isSent = rawMsg.LabelIds?.Any(x =>
                string.Equals(x, "SENT", StringComparison.OrdinalIgnoreCase)) == true;

            var internalDateRome = ParseInternalDate(rawMsg.InternalDate);

            int emailId;

            if (isSent)
            {
                emailId = await SaveGmailSentAsync(
                    conn,
                    casellaId,
                    mailboxEmail,
                    gmailMessageId: rawMsg.Id,
                    gmailThreadId: rawMsg.ThreadId,
                    mime,
                    internalDateRome,
                    ct);

                await SaveGmailSentAttachmentsAsync(
                    conn,
                    casellaId,
                    emailId,
                    mime,
                    ct);
            }
            else
            {
                emailId = await SaveGmailReceivedAsync(
                    conn,
                    casellaId,
                    gmailMessageId: rawMsg.Id,
                    gmailThreadId: rawMsg.ThreadId,
                    mime,
                    internalDateRome,
                    ct);
                await ApplyRulesForGmailReceivedAsync(
                    conn,
                    emailId,
                    mime,
                    internalDateRome,
                    mailboxEmail,
                    ct);
                await SaveGmailReceivedAttachmentsAsync(
                    conn,
                    casellaId,
                    emailId,
                    mime,
                    ct);
            }

            _logger.LogInformation(
                "Gmail thread sync: salvato msg Gmail={GmailMsgId}, thread={ThreadId}, emailId={EmailId}, sent={Sent}",
                rawMsg.Id,
                rawMsg.ThreadId,
                emailId,
                isSent);
        }
    }
    private async Task ApplyRulesForGmailReceivedAsync(
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
            var soloInvio = !reader.IsDBNull(5) && reader.GetString(5) == "Y";

            if (CheckRuleMatch(message, mittLike, destLike, oggLike, currentMailbox))
            {
                await AssignEmail(conn, emailId, utenti, soloInvio, ct);
                _logger.LogInformation(
                    "Gmail API: applicata regola {RuleId} su emailId={EmailId}",
                    id,
                    emailId);
            }
        }
    }
    private static bool CheckRuleMatch(
    MimeMessage msg,
    string? mitt,
    string? dest,
    string? subj,
    string? currentMailbox)
    {
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

    private async Task AssignEmail(
        OracleConnection conn,
        int emailId,
        string? utenti,
        bool soloInvio,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(utenti))
            return;

        var arr = utenti.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        const string sql = @"
MERGE INTO SGAPP.EMAIL_ASSEGNAZIONI t
USING (
    SELECT :p_eid AS EMAIL_ID,
           :p_user AS UTENTE,
           :p_solo AS SOLO_INVIO
    FROM dual
) s
ON (
    t.EMAIL_ID = s.EMAIL_ID
    AND t.UTENTE = s.UTENTE
    AND t.SOLO_INVIO = s.SOLO_INVIO
)
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
    private GmailService CreateGmailService(string userEmail)
    {
        var jsonPath = _config.GetValue<string>("GmailApi:ServiceAccountJsonPath");

        if (string.IsNullOrWhiteSpace(jsonPath) || !File.Exists(jsonPath))
            throw new FileNotFoundException("Service account Gmail API non trovato.", jsonPath);

        var credential = GoogleCredential
            .FromFile(jsonPath)
            .CreateScoped(GmailScopes)
            .CreateWithUser(userEmail);

        return new GmailService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = _config.GetValue<string>("GmailApi:ApplicationName") ?? "SCemail"
        });
    }

    private static async Task<bool> GmailMessageAlreadyExistsAsync(
        OracleConnection conn,
        string gmailMessageId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT COUNT(*)
FROM (
    SELECT ID FROM SGAPP.EMAIL_RICEVUTE WHERE GMAIL_MESSAGE_ID = :p_id
    UNION ALL
    SELECT ID FROM SGAPP.EMAIL_INVIATE WHERE GMAIL_MESSAGE_ID = :p_id
)";

        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
        cmd.Parameters.Add("p_id", OracleDbType.Varchar2, 128).Value = gmailMessageId;

        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        return count > 0;
    }

    private static async Task EnsureGmailThreadKeyAsync(
        OracleConnection conn,
        string gmailMessageId,
        string gmailThreadId,
        CancellationToken ct)
    {
        const string updRicevute = @"
UPDATE SGAPP.EMAIL_RICEVUTE
   SET GMAIL_THREAD_ID = :p_thread,
       THREAD_KEY = :p_thread
 WHERE GMAIL_MESSAGE_ID = :p_msg";

        await using (var cmd = new OracleCommand(updRicevute, conn) { BindByName = true })
        {
            cmd.Parameters.Add("p_thread", OracleDbType.Varchar2, 128).Value = gmailThreadId;
            cmd.Parameters.Add("p_msg", OracleDbType.Varchar2, 128).Value = gmailMessageId;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        const string updInviate = @"
UPDATE SGAPP.EMAIL_INVIATE
   SET GMAIL_THREAD_ID = :p_thread,
       THREAD_KEY = :p_thread
 WHERE GMAIL_MESSAGE_ID = :p_msg";

        await using (var cmd = new OracleCommand(updInviate, conn) { BindByName = true })
        {
            cmd.Parameters.Add("p_thread", OracleDbType.Varchar2, 128).Value = gmailThreadId;
            cmd.Parameters.Add("p_msg", OracleDbType.Varchar2, 128).Value = gmailMessageId;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<int> SaveGmailReceivedAsync(
        OracleConnection conn,
        int casellaId,
        string gmailMessageId,
        string gmailThreadId,
        MimeMessage message,
        DateTime dataRicezione,
        CancellationToken ct)
    {
        var messageId = NormalizeMessageId(message.MessageId) ?? BuildStableFallbackMessageId(message);
        var subject = NormalizeSubject(message.Subject, message.From?.ToString(), dataRicezione);

        const string sql = @"
INSERT INTO SGAPP.EMAIL_RICEVUTE
    (CASELLA_ID, MESSAGE_ID, DATA_RICEZIONE, MITTENTE, DESTINATARI, CC, CCN,
     OGGETTO, CORPO_HTML, CORPO_TESTO, APERTO, ELIMINATO, FOLDER_PATH, MESSAGE_UID,
     IN_REPLY_TO, REFERENCES_HDR, THREAD_KEY, BLACKLIST,
     GMAIL_MESSAGE_ID, GMAIL_THREAD_ID)
VALUES
    (:p_cid, :p_mid, :p_dt, :p_from, :p_to, :p_cc, :p_ccn,
     :p_subj, :p_html, :p_text, 'N', 'N', 'GMAIL_API', NULL,
     :p_inreply, :p_refs, :p_thread, 'N',
     :p_gmail_msg, :p_gmail_thread)
RETURNING ID INTO :p_id";

        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };

        cmd.Parameters.Add("p_cid", OracleDbType.Int32).Value = casellaId;
        cmd.Parameters.Add("p_mid", OracleDbType.Varchar2, 500).Value = messageId;
        cmd.Parameters.Add("p_dt", OracleDbType.Date).Value = dataRicezione;
        cmd.Parameters.Add("p_from", OracleDbType.Varchar2, 500).Value = message.From?.ToString() ?? "";
        cmd.Parameters.Add("p_to", OracleDbType.Varchar2, 2000).Value = message.To?.ToString() ?? "";
        cmd.Parameters.Add("p_cc", OracleDbType.Varchar2, 2000).Value = message.Cc?.ToString() ?? "";
        cmd.Parameters.Add("p_ccn", OracleDbType.Varchar2, 2000).Value = message.Bcc?.ToString() ?? "";
        cmd.Parameters.Add("p_subj", OracleDbType.Varchar2, 1000).Value = subject;
        cmd.Parameters.Add("p_html", OracleDbType.Clob).Value = (object?)message.HtmlBody ?? DBNull.Value;
        cmd.Parameters.Add("p_text", OracleDbType.Clob).Value = (object?)message.TextBody ?? DBNull.Value;
        cmd.Parameters.Add("p_inreply", OracleDbType.Varchar2, 500).Value =
            !string.IsNullOrWhiteSpace(message.InReplyTo)
                ? NormalizeMessageId(message.InReplyTo)
                : DBNull.Value;
        cmd.Parameters.Add("p_refs", OracleDbType.Clob).Value =
            message.References != null && message.References.Any()
                ? string.Join(" ", message.References.Select(NormalizeMessageId))
                : DBNull.Value;
        cmd.Parameters.Add("p_thread", OracleDbType.Varchar2, 500).Value = gmailThreadId;
        cmd.Parameters.Add("p_gmail_msg", OracleDbType.Varchar2, 128).Value = gmailMessageId;
        cmd.Parameters.Add("p_gmail_thread", OracleDbType.Varchar2, 128).Value = gmailThreadId;

        var outId = new OracleParameter("p_id", OracleDbType.Int32)
        {
            Direction = ParameterDirection.Output
        };

        cmd.Parameters.Add(outId);
        await cmd.ExecuteNonQueryAsync(ct);

        return ToInt(outId.Value);
    }

    private static async Task<int> SaveGmailSentAsync(
        OracleConnection conn,
        int casellaId,
        string mailboxEmail,
        string gmailMessageId,
        string gmailThreadId,
        MimeMessage message,
        DateTime dataInvio,
        CancellationToken ct)
    {
        var messageId = NormalizeMessageId(message.MessageId) ?? BuildStableFallbackMessageId(message);
        var subject = NormalizeSubject(message.Subject, message.From?.ToString(), dataInvio);

        const string sql = @"
INSERT INTO SGAPP.EMAIL_INVIATE
    (CASELLA_ID, UTENTE, DESTINATARI, CC, BCC, OGGETTO,
     CORPO_HTML, CORPO_TESTO, DATA_INVIO,
     MESSAGE_ID, IN_REPLY_TO, REFERENCES_HDR, THREAD_KEY,
     GMAIL_MESSAGE_ID, GMAIL_THREAD_ID)
VALUES
    (:p_cid, :p_user, :p_to, :p_cc, :p_bcc, :p_subj,
     :p_html, :p_text, :p_dt,
     :p_mid, :p_inreply, :p_refs, :p_thread,
     :p_gmail_msg, :p_gmail_thread)
RETURNING ID INTO :p_id";

        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };

        cmd.Parameters.Add("p_cid", OracleDbType.Int32).Value = casellaId;
        cmd.Parameters.Add("p_user", OracleDbType.Varchar2, 255).Value = mailboxEmail;
        cmd.Parameters.Add("p_to", OracleDbType.Varchar2, 2000).Value = message.To?.ToString() ?? "";
        cmd.Parameters.Add("p_cc", OracleDbType.Varchar2, 2000).Value = message.Cc?.ToString() ?? "";
        cmd.Parameters.Add("p_bcc", OracleDbType.Varchar2, 2000).Value = message.Bcc?.ToString() ?? "";
        cmd.Parameters.Add("p_subj", OracleDbType.Varchar2, 1000).Value = subject;
        cmd.Parameters.Add("p_html", OracleDbType.Clob).Value = (object?)message.HtmlBody ?? DBNull.Value;
        cmd.Parameters.Add("p_text", OracleDbType.Clob).Value = (object?)message.TextBody ?? DBNull.Value;
        cmd.Parameters.Add("p_dt", OracleDbType.Date).Value = dataInvio;
        cmd.Parameters.Add("p_mid", OracleDbType.Varchar2, 500).Value = messageId;
        cmd.Parameters.Add("p_inreply", OracleDbType.Varchar2, 500).Value =
            !string.IsNullOrWhiteSpace(message.InReplyTo)
                ? NormalizeMessageId(message.InReplyTo)
                : DBNull.Value;
        cmd.Parameters.Add("p_refs", OracleDbType.Clob).Value =
            message.References != null && message.References.Any()
                ? string.Join(" ", message.References.Select(NormalizeMessageId))
                : DBNull.Value;
        cmd.Parameters.Add("p_thread", OracleDbType.Varchar2, 500).Value = gmailThreadId;
        cmd.Parameters.Add("p_gmail_msg", OracleDbType.Varchar2, 128).Value = gmailMessageId;
        cmd.Parameters.Add("p_gmail_thread", OracleDbType.Varchar2, 128).Value = gmailThreadId;

        var outId = new OracleParameter("p_id", OracleDbType.Int32)
        {
            Direction = ParameterDirection.Output
        };

        cmd.Parameters.Add(outId);
        await cmd.ExecuteNonQueryAsync(ct);

        return ToInt(outId.Value);
    }

    private async Task SaveGmailReceivedAttachmentsAsync(
        OracleConnection conn,
        int casellaId,
        int emailId,
        MimeMessage message,
        CancellationToken ct)
    {
        var index = 0;

        foreach (var entity in message.Attachments)
        {
            index++;

            var fileName = GetAttachmentFileName(entity, index);
            var mime = NormalizeAttachmentMime(entity.ContentType?.MimeType, fileName);
            var isEml = entity is MimeKit.MessagePart ||
                        fileName.EndsWith(".eml", StringComparison.OrdinalIgnoreCase) ||
                        mime.Equals("message/rfc822", StringComparison.OrdinalIgnoreCase);

            var allegatoId = await InsertReceivedAttachmentMetadataAsync(
                conn,
                emailId,
                fileName,
                mime,
                $"gmail-{index}",
                ct);

            await StoreAttachmentFileAsync(
                conn,
                tableName: "SGAPP.EMAIL_ALLEGATI",
                idColumn: "ID",
                casellaId,
                emailId,
                allegatoId,
                fileName,
                entity,
                isEml,
                ct);
        }
    }

    private async Task SaveGmailSentAttachmentsAsync(
        OracleConnection conn,
        int casellaId,
        int emailId,
        MimeMessage message,
        CancellationToken ct)
    {
        var index = 0;

        foreach (var entity in message.Attachments)
        {
            index++;

            var fileName = GetAttachmentFileName(entity, index);
            var mime = NormalizeAttachmentMime(entity.ContentType?.MimeType, fileName);

            var allegatoId = await InsertSentAttachmentMetadataAsync(
                conn,
                emailId,
                fileName,
                mime,
                $"gmail-{index}",
                ct);

            await StoreAttachmentFileAsync(
                conn,
                tableName: "SGAPP.INVIATA_ALLEGATI",
                idColumn: "ID",
                casellaId,
                emailId,
                allegatoId,
                fileName,
                entity,
                false,
                ct);
        }
    }

    private static async Task<int> InsertReceivedAttachmentMetadataAsync(
        OracleConnection conn,
        int emailId,
        string fileName,
        string mime,
        string partSpec,
        CancellationToken ct)
    {
        const string existsSql = @"
SELECT ID
FROM SGAPP.EMAIL_ALLEGATI
WHERE EMAIL_ID = :p_email
  AND PART_SPEC = :p_part
FETCH FIRST 1 ROWS ONLY";

        await using (var exists = new OracleCommand(existsSql, conn) { BindByName = true })
        {
            exists.Parameters.Add("p_email", OracleDbType.Int32).Value = emailId;
            exists.Parameters.Add("p_part", OracleDbType.Varchar2, 64).Value = partSpec;

            var existing = await exists.ExecuteScalarAsync(ct);
            if (existing != null && existing != DBNull.Value)
                return Convert.ToInt32(existing.ToString());
        }

        const string sql = @"
INSERT INTO SGAPP.EMAIL_ALLEGATI
    (EMAIL_ID, NOME_FILE, MIME_TYPE, PART_SPEC)
VALUES
    (:p_email, :p_name, :p_mime, :p_part)
RETURNING ID INTO :p_id";

        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
        cmd.Parameters.Add("p_email", OracleDbType.Int32).Value = emailId;
        cmd.Parameters.Add("p_name", OracleDbType.Varchar2, 512).Value = fileName;
        cmd.Parameters.Add("p_mime", OracleDbType.Varchar2, 255).Value = mime;
        cmd.Parameters.Add("p_part", OracleDbType.Varchar2, 64).Value = partSpec;

        var outId = new OracleParameter("p_id", OracleDbType.Int32)
        {
            Direction = ParameterDirection.Output
        };

        cmd.Parameters.Add(outId);
        await cmd.ExecuteNonQueryAsync(ct);

        return ToInt(outId.Value);
    }

    private static async Task<int> InsertSentAttachmentMetadataAsync(
        OracleConnection conn,
        int emailId,
        string fileName,
        string mime,
        string partSpec,
        CancellationToken ct)
    {
        const string existsSql = @"
SELECT ID
FROM SGAPP.INVIATA_ALLEGATI
WHERE EMAIL_ID = :p_email
  AND PART_SPEC = :p_part
FETCH FIRST 1 ROWS ONLY";

        await using (var exists = new OracleCommand(existsSql, conn) { BindByName = true })
        {
            exists.Parameters.Add("p_email", OracleDbType.Int32).Value = emailId;
            exists.Parameters.Add("p_part", OracleDbType.Varchar2, 64).Value = partSpec;

            var existing = await exists.ExecuteScalarAsync(ct);
            if (existing != null && existing != DBNull.Value)
                return Convert.ToInt32(existing.ToString());
        }

        const string sql = @"
INSERT INTO SGAPP.INVIATA_ALLEGATI
    (EMAIL_ID, NOME_FILE, MIME_TYPE, PART_SPEC)
VALUES
    (:p_email, :p_name, :p_mime, :p_part)
RETURNING ID INTO :p_id";

        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
        cmd.Parameters.Add("p_email", OracleDbType.Int32).Value = emailId;
        cmd.Parameters.Add("p_name", OracleDbType.Varchar2, 512).Value = fileName;
        cmd.Parameters.Add("p_mime", OracleDbType.Varchar2, 255).Value = mime;
        cmd.Parameters.Add("p_part", OracleDbType.Varchar2, 64).Value = partSpec;

        var outId = new OracleParameter("p_id", OracleDbType.Int32)
        {
            Direction = ParameterDirection.Output
        };

        cmd.Parameters.Add(outId);
        await cmd.ExecuteNonQueryAsync(ct);

        return ToInt(outId.Value);
    }

    private async Task StoreAttachmentFileAsync(
        OracleConnection conn,
        string tableName,
        string idColumn,
        int casellaId,
        int emailId,
        int allegatoId,
        string fileName,
        MimeEntity entity,
        bool isEml,
        CancellationToken ct)
    {
        var safeName = SanitizeFileName(fileName);
        var emailDir = Path.Combine(_attachmentsBasePath, casellaId.ToString(), emailId.ToString());
        Directory.CreateDirectory(emailDir);

        var relPath = Path.Combine(casellaId.ToString(), emailId.ToString(), $"{allegatoId}_{safeName}");
        var absPath = Path.Combine(_attachmentsBasePath, relPath);

        long size;

        await using (var fs = new FileStream(
            absPath,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.None,
            81920,
            useAsync: true))
        {
            if (entity is MimePart mp)
                await mp.Content.DecodeToAsync(fs, ct);
            else if (entity is MimeKit.MessagePart msgPart)
                await msgPart.Message.WriteToAsync(fs, ct);
            else
                await entity.WriteToAsync(fs, ct);

            await fs.FlushAsync(ct);
            size = fs.Length;
        }

        var sql = $@"
UPDATE {tableName}
   SET FILE_PATH = :p_path,
       FILE_SIZE = :p_size
 WHERE {idColumn} = :p_id";

        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
        cmd.Parameters.Add("p_path", OracleDbType.Varchar2, 1024).Value = relPath;
        cmd.Parameters.Add("p_size", OracleDbType.Int64).Value = size;
        cmd.Parameters.Add("p_id", OracleDbType.Int32).Value = allegatoId;

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static DateTime ParseInternalDate(long? internalDate)
    {
        if (!internalDate.HasValue)
            return TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, RomeTz).DateTime;

        var dto = DateTimeOffset.FromUnixTimeMilliseconds(internalDate.Value);
        return TimeZoneInfo.ConvertTime(dto, RomeTz).DateTime;
    }

    private static byte[] DecodeBase64Url(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');

        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }

        return Convert.FromBase64String(s);
    }

    private static string? NormalizeMessageId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim().Trim('<', '>', ' ', '\t', '\r', '\n');
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

    private static string NormalizeSubject(string? subject, string? from, DateTime dt)
    {
        var s = subject?.Trim();

        if (string.IsNullOrWhiteSpace(s))
            s = $"(Senza oggetto) da {(string.IsNullOrWhiteSpace(from) ? "Sconosciuto" : from)} - {dt:yyyy-MM-dd HH:mm}";

        return s.Length > 1000 ? s[..1000] : s;
    }

    private static string GetAttachmentFileName(MimeEntity entity, int index)
    {
        if (entity is MimePart mp && !string.IsNullOrWhiteSpace(mp.FileName))
            return mp.FileName;

        if (entity is MimeKit.MessagePart)
            return $"email_allegata_{index}.eml";

        return $"allegato_{index}";
    }

    private static string NormalizeAttachmentMime(string? mimeType, string? fileName)
    {
        var mime = (mimeType ?? "").Trim();

        var byExt = Path.GetExtension(fileName ?? "").ToLowerInvariant() switch
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
            return mime;

        return Path.GetExtension(fileName ?? "").ToLowerInvariant() switch
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
            _ => string.IsNullOrWhiteSpace(mime) ? "application/octet-stream" : mime
        };
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');

        name = name.Replace("..", "_").Trim();

        return name.Length > 200 ? name[..200] : name;
    }

    private static int ToInt(object value)
    {
        if (value is Oracle.ManagedDataAccess.Types.OracleDecimal od)
            return od.ToInt32();

        return Convert.ToInt32(value.ToString());
    }
}