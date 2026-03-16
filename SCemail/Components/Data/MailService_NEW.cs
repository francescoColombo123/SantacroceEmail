using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MudBlazor.Charts;
using Oracle.ManagedDataAccess.Client;
using SCemail.Components.Data;
using SCemail.Components.Shared;
using System.Data;
using System.Linq;
using System.Text.RegularExpressions;
using static MudBlazor.CategoryTypes;
using static Org.BouncyCastle.Math.EC.ECCurve;
using static SCemail.Components.Pages.MailDetail;


namespace SCemail.Components.Data
{
    public class MailService_NEW
    {

        private readonly HttpClient _http;
        private readonly IDbContextFactory<MailDbContext> _dbFactory;
        private readonly ILogger<MailService_NEW> _logger;
        private readonly AccessiService _accessiService;
        private readonly string _connectionString;

        private readonly IOptions<AttachmentsOptions> _attachmentsOpt;
        public MailService_NEW(IDbContextFactory<MailDbContext> dbFactory,
                           ILogger<MailService_NEW> logger,
                           HttpClient http, IConfiguration config, AccessiService accessiService, IOptions<AttachmentsOptions> attachmentsOpt
                           )
        {
            _dbFactory = dbFactory;
            _logger = logger;
            _http = http;
            _connectionString = config.GetConnectionString("OracleDb")
                ?? throw new InvalidOperationException("Connection string 'OracleDb' mancante nel file di configurazione.");
            _accessiService = accessiService;
            _attachmentsOpt = attachmentsOpt;

        }
        public async Task<List<string>> GetEmailAddressesByIdsAsync(List<int> ids)
        {
            if (ids == null || ids.Count == 0)
                return new List<string>();

            await using var db = _dbFactory.CreateDbContext();

            return await db.CasellePosta
                .AsNoTracking()
                .Where(c => ids.Contains(c.Id))
                .Select(c => c.Email)
                .Distinct()
                .ToListAsync();
        }


        public async Task UnarchiveEmailAsync(int emailId, string utente)
        {
            using var con = new OracleConnection(_connectionString);
            await con.OpenAsync();

            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
BEGIN
    DELETE FROM SGAPP.EMAIL_ARCHIVIO
     WHERE ID_EMAIL = :emailId
       AND UTENTE = :utente;
END;";

            cmd.Parameters.Add(new OracleParameter("emailId", emailId));
            cmd.Parameters.Add(new OracleParameter("utente", utente));

            await cmd.ExecuteNonQueryAsync();
        }


        public async Task MarkUnreadAsync(int emailId)
        {
            using var con = new OracleConnection(_connectionString);
            await con.OpenAsync();

            using var cmd = con.CreateCommand();
            cmd.CommandText = @"UPDATE SGAPP.EMAIL_RICEVUTE SET APERTO='N' WHERE ID=:emailId";
            cmd.Parameters.Add(new OracleParameter("emailId", emailId));
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task MarkReadAsync(int emailId)
        {
            using var con = new OracleConnection(_connectionString);
            await con.OpenAsync();

            using var cmd = con.CreateCommand();
            cmd.CommandText = @"UPDATE SGAPP.EMAIL_RICEVUTE SET APERTO='Y' WHERE ID=:emailId";
            cmd.Parameters.Add(new OracleParameter("emailId", emailId));
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<(List<EmailListItem_NEW>, int)> SearchEmailsAsync(
     List<int> casellaIds,
     int start,
     int pageSize,
     string searchText)
        {
            using var conn = new OracleConnection(_connectionString);
            await conn.OpenAsync();

            // ⚠️ NOTA:
            // - niente :start (parola riservata)
            // - preview coerente
            // - COUNT(*) OVER() per il totale
            var sql = $@"
SELECT
    e.ID,
    e.MITTENTE,
    e.OGGETTO,
    e.DATA_RICEZIONE,
    e.CASELLA_ID,
    c.EMAIL AS CASELLA_EMAIL,
    e.APERTO,
    CASE WHEN EXISTS (
        SELECT 1 FROM SGAPP.EMAIL_ALLEGATI a WHERE a.EMAIL_ID = e.ID
    ) THEN 1 ELSE 0 END AS HAS_ATTACH,
    SUBSTR(
        REGEXP_REPLACE(NVL(e.CORPO_TESTO, e.CORPO_HTML), '<[^>]+>', ''),
        1, 200
    ) AS PREVIEW,
    COUNT(*) OVER() AS TOTAL_COUNT
FROM SGAPP.EMAIL_RICEVUTE e
JOIN SGAPP.CASELLEPOSTA c ON c.ID = e.CASELLA_ID
WHERE e.CASELLA_ID IN ({string.Join(",", casellaIds)})
  AND (
        LOWER(e.MITTENTE) LIKE :p_q
     OR LOWER(e.OGGETTO)  LIKE :p_q
  )
ORDER BY e.DATA_RICEZIONE DESC
OFFSET :p_offset ROWS FETCH NEXT :p_limit ROWS ONLY";

            using var cmd = new OracleCommand(sql, conn) { BindByName = true };

            cmd.Parameters.Add("p_q", OracleDbType.Varchar2)
                .Value = "%" + searchText.ToLowerInvariant() + "%";

            cmd.Parameters.Add("p_offset", OracleDbType.Int32).Value = start;
            cmd.Parameters.Add("p_limit", OracleDbType.Int32).Value = pageSize;

            var list = new List<EmailListItem_NEW>();
            int total = 0;

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (total == 0)
                    total = reader.GetInt32(reader.GetOrdinal("TOTAL_COUNT"));

                list.Add(new EmailListItem_NEW(
                    Id: reader.GetInt32("ID"),
                    Data: reader.GetDateTime("DATA_RICEZIONE"),
                    Mittente: GetStr(reader, "MITTENTE") ?? "",
                    Oggetto: GetStr(reader, "OGGETTO") ?? "(senza oggetto)",
                    Aperto: reader.IsDBNull("APERTO") ? "" : reader.GetString("APERTO"),
                    HasAttachments: reader.GetInt32("HAS_ATTACH") == 1,
                    ThreadLen: 1,
                    Replies: 0,
                    Preview: reader.IsDBNull("PREVIEW") ? null : reader.GetString("PREVIEW"),
                    Allegati: null,
                    MessageId: null,
                    CasellaId: reader.GetInt32("CASELLA_ID"),
                    CasellaEmail: reader.GetString("CASELLA_EMAIL"),
                    AssegnatoA: null
                ));
            }

            return (list, total);
        }
        public async Task<(List<EmailListItem_NEW>, int)> GetMentionedEmailsPagedSafeAsync(
    string utente,
    int start = 0,
    int pageSize = 50)
        {
            await using var db = _dbFactory.CreateDbContext();

            //
            // 1️⃣ Recupero ID email + conteggio NON VISTE (solo NUMERI)
            //
            var baseQuery =
                from m in db.emailMenzionis
                where m.Utente == utente
                group m by m.EmailId
                into g
                select new
                {
                    EmailId = g.Key,
                    UnreadCount = g.Count(x => x.Visto == "N"),
                    LastMentionDate = g.Max(x => x.DataMenzione)
                };

            var total = await baseQuery.CountAsync();

            var pageKeys = await baseQuery
                .OrderByDescending(x => x.LastMentionDate)
                .Skip(start)
                .Take(pageSize)
                .ToListAsync();

            var emailIds = pageKeys.Select(x => x.EmailId).ToList();

            //
            // 2️⃣ Carico le email (query SEMPLICE)
            //
            var emails = await (
                from e in db.EmailRicevute
                join c in db.CasellePosta on e.CasellaId equals c.Id
                where emailIds.Contains(e.Id)
                select new
                {
                    e.Id,
                    e.DataRicezione,
                    e.Mittente,
                    e.Oggetto,
                    e.CorpoHtml,
                    e.CorpoTesto,
                    e.CasellaId,
                    CasellaEmail = c.Email
                }
            ).ToListAsync();

            //
            // 3️⃣ Mapping finale IN MEMORIA
            //
            var unreadMap = pageKeys.ToDictionary(x => x.EmailId, x => x.UnreadCount);

            var result = emails
                .OrderByDescending(e => pageKeys.First(x => x.EmailId == e.Id).LastMentionDate)
                .Select(e =>
                {
                    unreadMap.TryGetValue(e.Id, out var unread);

                    var preview = !string.IsNullOrWhiteSpace(e.CorpoTesto)
                        ? e.CorpoTesto
                        : e.CorpoHtml;

                    if (!string.IsNullOrEmpty(preview) && preview.Length > 200)
                        preview = preview[..200];

                    return new EmailListItem_NEW(
                        Id: e.Id,
                        Data: e.DataRicezione,
                        Mittente: e.Mittente ?? "",
                        Oggetto: e.Oggetto ?? "",
                        Aperto: unread > 0 ? "N" : "Y",   // ⭐ SOLO QUI la logica
                        HasAttachments: false,
                        ThreadLen: 1,
                        Replies: 0,
                        Preview: preview,
                        Allegati: null,
                        MessageId: null,
                        CasellaId: e.CasellaId,
                        CasellaEmail: e.CasellaEmail ?? "",
                        AssegnatoA: utente
                    );
                })
                .ToList();

            return (result, total);
        }

        private string BuildPreview(string? html, string? text)
        {
            var source = !string.IsNullOrWhiteSpace(html) ? html : text;

            if (string.IsNullOrWhiteSpace(source))
                return "";

            return source.Length > 180
                ? source.Substring(0, 180) + "…"
                : source;
        }

        public async Task<EmailReplyInfo?> GetReplyInfoAsync(string? threadKey, string? inReplyTo)
        {
            if (string.IsNullOrWhiteSpace(threadKey) && string.IsNullOrWhiteSpace(inReplyTo))
                return null;

            using var conn = new OracleConnection(_connectionString);
            await conn.OpenAsync();

            // 1) Prova da EMAIL_INVIATE (se esiste qualcosa del thread)
            {
                var sql = @"
SELECT
    ci.EMAIL AS CASELLA_MITTENTE,
    ei.DESTINATARI,
    ei.CC,
    ei.BCC
FROM SGAPP.EMAIL_INVIATE ei
JOIN SGAPP.CASELLEPOSTA ci ON ci.ID = ei.CASELLA_ID
WHERE ( :threadKey IS NOT NULL AND ei.THREAD_KEY = :threadKey )
   OR ( :inReplyTo IS NOT NULL AND ei.MESSAGE_ID = :inReplyTo )
ORDER BY ei.DATA_INVIO DESC
FETCH FIRST 1 ROWS ONLY";

                await using var cmd = new OracleCommand(sql, conn) { BindByName = true };

                cmd.Parameters.Add("threadKey", OracleDbType.Varchar2).Value =
                    string.IsNullOrWhiteSpace(threadKey) ? DBNull.Value : threadKey;

                cmd.Parameters.Add("inReplyTo", OracleDbType.Varchar2).Value =
                    string.IsNullOrWhiteSpace(inReplyTo) ? DBNull.Value : inReplyTo;

                await using var r = await cmd.ExecuteReaderAsync();
                if (await r.ReadAsync())
                {
                    return new EmailReplyInfo
                    {
                        CasellaMittente = r.IsDBNull(0) ? "" : r.GetString(0),
                        Destinatari = r.IsDBNull(1) ? "" : r.GetString(1),
                        Cc = r.IsDBNull(2) ? null : r.GetString(2),
                        Bcc = r.IsDBNull(3) ? null : r.GetString(3),
                    };
                }
            }

            // 2) Fallback: EMAIL_RICEVUTE (serve per ReplyAll su mail ricevute)
            {
                // qui BCC di solito non lo hai sulle ricevute -> uso CCN se presente
                var sql = @"
SELECT
    c.EMAIL AS CASELLA_MITTENTE,
    e.DESTINATARI,
    e.CC,
    e.CCN
FROM SGAPP.EMAIL_RICEVUTE e
JOIN SGAPP.CASELLEPOSTA c ON c.ID = e.CASELLA_ID
WHERE ( :threadKey IS NOT NULL AND e.THREAD_KEY = :threadKey )
   OR ( :inReplyTo IS NOT NULL AND e.MESSAGE_ID = :inReplyTo )
ORDER BY e.DATA_RICEZIONE DESC
FETCH FIRST 1 ROWS ONLY";

                await using var cmd = new OracleCommand(sql, conn) { BindByName = true };

                cmd.Parameters.Add("threadKey", OracleDbType.Varchar2).Value =
                    string.IsNullOrWhiteSpace(threadKey) ? DBNull.Value : threadKey;

                cmd.Parameters.Add("inReplyTo", OracleDbType.Varchar2).Value =
                    string.IsNullOrWhiteSpace(inReplyTo) ? DBNull.Value : inReplyTo;

                await using var r = await cmd.ExecuteReaderAsync();
                if (!await r.ReadAsync())
                    return null;

                return new EmailReplyInfo
                {
                    CasellaMittente = r.IsDBNull(0) ? "" : r.GetString(0),
                    Destinatari = r.IsDBNull(1) ? "" : r.GetString(1),
                    Cc = r.IsDBNull(2) ? null : r.GetString(2),
                    Bcc = r.IsDBNull(3) ? null : r.GetString(3), // qui è CCN
                };
            }
        }


        public record CommentoEmail(int Id, string Testo, DateTime DataCreazione, string Autore);
        public record TaskHomeBadgeItem(
                 int TaskId,
                 string Titolo,
                 DateTime DataCreazione,
                 DateTime? DataScadenza,
                 string CreatoDa
             );

        public record TaskHomeBadgesDto(
            int ToCloseCount,
            List<TaskHomeBadgeItem> ToCloseTop,
            int WaitingReviewCount,
            List<TaskHomeBadgeItem> WaitingReviewTop
        );
        public async Task<List<AdminEmailDto>> GetEmailInLavorazioneAsync()
        {
            using var db = _dbFactory.CreateDbContext();

            var raw = await (
                from a in db.EmailAssegnazione
                join e in db.EmailRicevute on a.EmailId equals e.Id
                join ar in db.EmailArchivio on e.Id equals ar.IdEmail into arj
                from ar in arj.DefaultIfEmpty()
                where ar == null
                orderby e.DataRicezione descending
                select new
                {
                    e.Id,
                    e.Oggetto,
                    e.Mittente,
                    e.DataRicezione,
                    a.Utente
                }
            )
            .Take(500)
            .AsNoTracking()
            .ToListAsync();

            // ⬇️ QUI, IN MEMORIA
            return raw.Select(x => new AdminEmailDto
            {
                EmailId = x.Id,
                Oggetto = x.Oggetto,
                Mittente = x.Mittente,
                Data = x.DataRicezione,
                AssegnatoA = x.Utente,
                Completata = false
            }).ToList();
        }


        public async Task<List<AdminEmailDto>> GetEmailCompletateAsync()
        {
            using var db = _dbFactory.CreateDbContext();

            var raw = await (
                from ar in db.EmailArchivio
                join e in db.EmailRicevute on ar.IdEmail equals e.Id
                orderby ar.DataArchiviazione descending
                select new
                {
                    e.Id,
                    e.Oggetto,
                    e.Mittente,
                    ar.DataArchiviazione,
                    ar.Utente
                }
            )
            .Take(500)
            .AsNoTracking()
            .ToListAsync();

            return raw.Select(x => new AdminEmailDto
            {
                EmailId = x.Id,
                Oggetto = x.Oggetto,
                Mittente = x.Mittente,
                Data = x.DataArchiviazione,
                AssegnatoA = x.Utente,
                Completata = true
            }).ToList();
        }


        public async Task<List<CommentoEmail>> GetCommentsByEmailAsync(int emailId)
        {
            var list = new List<CommentoEmail>();
            using var conn = new OracleConnection(_connectionString);
            await conn.OpenAsync();

            string sql = @"
        SELECT T.ID, T.TESTO, T.DATA_CREAZIONE, T.UTENTE
        FROM EMAIL_TASK_COMMENTS T
        WHERE T.TASK_ID = :emailId
        ORDER BY T.DATA_CREAZIONE DESC";

            await using var cmd = new OracleCommand(sql, conn);
            cmd.Parameters.Add("emailId", OracleDbType.Int32).Value = emailId;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new CommentoEmail(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetDateTime(2),
                    reader.IsDBNull(3) ? "" : reader.GetString(3)
                ));
            }

            return list;
        }

        public async Task AssegnaUtenteAsync(int emailId, string utente)
        {
            await using var conn = await GetOpenConnectionAsync();

            const string sql = @"
        INSERT INTO SGAPP.EMAIL_ASSEGNAZIONI (EMAIL_ID, UTENTE)
        VALUES (:emailId, :utente)";

            await using var cmd = new OracleCommand(sql, conn);
            cmd.Parameters.Add("emailId", OracleDbType.Decimal).Value = emailId;
            cmd.Parameters.Add("utente", OracleDbType.Varchar2).Value = utente;

            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<List<string>> SearchRecipientsAsync(string utente, string query, int max = 10, CancellationToken ct = default)
        {
            var list = new List<string>();
            await using var conn = new OracleConnection(_connectionString);
            await conn.OpenAsync(ct);

            const string sql = @"
            SELECT email
            FROM (
                SELECT DISTINCT email
                FROM (
                    SELECT LOWER(TRIM(destinatario)) AS email
                    FROM sgapp.email_destinatari
                    WHERE destinatario LIKE '%@%'
                      AND LOWER(TRIM(destinatario)) LIKE '%' || :q || '%'

                    UNION

                    SELECT LOWER(TRIM(email)) AS email
                    FROM sgapp.rubrica_contatti
                    WHERE email LIKE '%@%'
                      AND LOWER(TRIM(email)) LIKE '%' || :q || '%'
                )
            )
            WHERE ROWNUM <= :max";

            await using var cmd = new OracleCommand(sql, conn);
            cmd.BindByName = true;
            cmd.Parameters.Add("q", OracleDbType.Varchar2).Value = (query ?? "").Trim().ToLowerInvariant();
            cmd.Parameters.Add("max", OracleDbType.Int32).Value = max;

            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                var email = rdr.IsDBNull(0) ? null : rdr.GetString(0)?.Trim();
                if (!string.IsNullOrWhiteSpace(email))
                    list.Add(email);
            }

            return list;
        }

        public async Task<string> GetEmailAddressByUsernameAsync(string username)
        {
            // 🔍 Trova l’indirizzo email associato all’utente
            const string sql = @"
        SELECT c.EMAIL
        FROM SGAPP.CASELLEPOSTA c
        JOIN SGAPP.CASELLA_ABILITAZIONI a ON a.CASELLA_ID = c.ID
        WHERE a.USERNAME = :username";

            await using var conn = new OracleConnection(_connectionString);
            await conn.OpenAsync();

            await using var cmd = new OracleCommand(sql, conn);
            cmd.Parameters.Add(new OracleParameter("username", OracleDbType.Varchar2) { Value = username });

            var result = await cmd.ExecuteScalarAsync();
            return result?.ToString() ?? username; // fallback se non trova nulla
        }

     
   public async Task<int> SaveSentWithThreadingAsync(
    OracleConnection conn,
    string messageId,
    string? inReplyTo,
    string mittente,
    string destinatari,
    string oggetto,
    string corpoHtml,
    string corpoTesto,
    DateTime dataInvioUtc)
        {
            string Normalize(string? s)
                => (s ?? "").Trim().ToUpperInvariant();

            string? threadKey = null;
            string inReplyNorm = Normalize(inReplyTo);

            // 1️⃣ Se è una risposta
            if (!string.IsNullOrEmpty(inReplyNorm))
            {
                // 🔹 Tentativo 1: match esatto
                const string sqlExact = @"
            SELECT THREAD_KEY FROM (
                SELECT MESSAGE_ID, THREAD_KEY FROM SGAPP.EMAIL_RICEVUTE
                UNION ALL
                SELECT MESSAGE_ID, THREAD_KEY FROM SGAPP.EMAIL_INVIATE
            )
            WHERE UPPER(TRIM(MESSAGE_ID)) = :mid";

                await using var cmd1 = new OracleCommand(sqlExact, conn) { BindByName = true };
                cmd1.Parameters.Add("mid", OracleDbType.Varchar2, 500).Value = inReplyNorm;

                var found = await cmd1.ExecuteScalarAsync();
                if (found != null && found != DBNull.Value)
                    threadKey = found.ToString();

                // 🔹 Tentativo 2: ricerca fuzzy (caso Gmail ↔ Aruba)
                if (string.IsNullOrEmpty(threadKey))
                {
                    string idBeforeAt = inReplyNorm.Split('@')[0];
                    string[] parts = idBeforeAt.Split('.', StringSplitOptions.RemoveEmptyEntries);
                    string core = parts.Length switch
                    {
                        >= 3 => parts[parts.Length / 2],
                        2 => parts[0].Length >= parts[1].Length ? parts[1] : parts[0],
                        _ => idBeforeAt
                    };

                    core = new string(core.Where(char.IsLetterOrDigit).ToArray());
                    string pattern = "%" + core + "%";

                    const string sqlLoose = @"
                SELECT THREAD_KEY FROM (
                    SELECT MESSAGE_ID, THREAD_KEY FROM SGAPP.EMAIL_RICEVUTE
                    UNION ALL
                    SELECT MESSAGE_ID, THREAD_KEY FROM SGAPP.EMAIL_INVIATE
                )
                WHERE UPPER(TRIM(MESSAGE_ID)) LIKE :pattern";

                    await using var cmd2 = new OracleCommand(sqlLoose, conn) { BindByName = true };
                    cmd2.Parameters.Add("pattern", OracleDbType.Varchar2, 500).Value = pattern;

                    var foundLoose = await cmd2.ExecuteScalarAsync();
                    if (foundLoose != null && foundLoose != DBNull.Value)
                        threadKey = foundLoose.ToString();
                }

                // 🔹 Tentativo 3: fallback → InReplyTo come chiave
                if (string.IsNullOrEmpty(threadKey))
                    threadKey = inReplyNorm;
            }

            // 2️⃣ Se non è una risposta, nuovo thread
            if (string.IsNullOrEmpty(threadKey))
                threadKey = Normalize(messageId);

            // 3️⃣ Salvataggio
            const string sql = @"
        INSERT INTO SGAPP.EMAIL_INVIATE
            (MESSAGE_ID, IN_REPLY_TO, OGGETTO, MITTENTE, DESTINATARI,
             CORPO_HTML, CORPO_TESTO, DATA_INVIO, THREAD_KEY)
        VALUES
            (:p_mid, :p_inr, :p_subj, :p_from, :p_to,
             :p_html, :p_text, :p_dt, :p_thread)
        RETURNING ID INTO :p_id";

            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
            cmd.Parameters.Add("p_mid", OracleDbType.Varchar2, 500).Value = messageId;
            cmd.Parameters.Add("p_inr", OracleDbType.Varchar2, 500).Value = (object?)inReplyTo ?? DBNull.Value;
            cmd.Parameters.Add("p_subj", OracleDbType.Varchar2, 1000).Value = oggetto ?? "";
            cmd.Parameters.Add("p_from", OracleDbType.Varchar2, 500).Value = mittente ?? "";
            cmd.Parameters.Add("p_to", OracleDbType.Varchar2, 2000).Value = destinatari ?? "";
            cmd.Parameters.Add("p_html", OracleDbType.Clob).Value = (object?)corpoHtml ?? DBNull.Value;
            cmd.Parameters.Add("p_text", OracleDbType.Clob).Value = (object?)corpoTesto ?? DBNull.Value;
            cmd.Parameters.Add("p_dt", OracleDbType.Date).Value = dataInvioUtc;
            cmd.Parameters.Add("p_thread", OracleDbType.Varchar2, 500).Value = threadKey;

            var outId = new OracleParameter("p_id", OracleDbType.Int32) { Direction = ParameterDirection.Output };
            cmd.Parameters.Add(outId);

            await cmd.ExecuteNonQueryAsync();

            return Convert.ToInt32(outId.Value.ToString());
        }


        public async Task UpdateFolderPathAsync(int emailId, string newFolder, CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();

            var email = await db.EmailRicevute.FirstOrDefaultAsync(e => e.Id == emailId, ct);
            if (email == null)
                throw new InvalidOperationException($"Email {emailId} non trovata");

            email.FolderPath = newFolder;
            await db.SaveChangesAsync(ct);

            _logger.LogInformation($"📂 Email {emailId} spostata in {newFolder}");
        }

        /* ================== HELPERS ================== */

        private static string NormalizeFolder(string s)
        {
            var t = s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
            t = Regex.Replace(t, @"\s+", " ");
            return t.ToUpperInvariant();
        }


        public async Task<List<string>> GetUtentiAsync(CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            return await db.InfoUsers 
                .AsNoTracking()
                .Where(u => !string.IsNullOrWhiteSpace(u.Utente))
                .Select(u => u.Utente!)
                .Distinct()
                .OrderBy(u => u)
                .ToListAsync(ct);
        }

        public async Task<List<EmailDetail_NEW>> GetConversationByThreadAsync(int emailId)
        {
            await using var conn = await GetOpenConnectionAsync();

            // 1️⃣ Recupera il THREAD_KEY del messaggio selezionato
            string threadKey;
            const string findThreadSql = @"
        SELECT THREAD_KEY FROM (
            SELECT THREAD_KEY FROM SGAPP.EMAIL_RICEVUTE WHERE ID = :id
            UNION ALL
            SELECT THREAD_KEY FROM SGAPP.EMAIL_INVIATE WHERE ID = :id
        ) WHERE ROWNUM = 1";

            await using (var findCmd = new OracleCommand(findThreadSql, conn))
            {
                findCmd.Parameters.Add("id", OracleDbType.Int32).Value = emailId;
                var result = await findCmd.ExecuteScalarAsync();
                threadKey = result?.ToString() ?? "";
            }

            if (string.IsNullOrEmpty(threadKey))
                return new();

            // 2️⃣ Prende TUTTE le email (ricevute + inviate) dello stesso thread
            const string sql = @"
SELECT 
    ID,
    TO_CLOB(MITTENTE) AS MITTENTE,
    TO_CLOB(DESTINATARI) AS DESTINATARI,
    TO_CLOB(OGGETTO) AS OGGETTO,
    DATA_RICEZIONE AS DATA,
    CORPO_HTML,
    CORPO_TESTO,
    MESSAGE_ID,
    IN_REPLY_TO,
    REFERENCES_HDR,
    THREAD_KEY,
    'R' AS TIPO
  FROM SGAPP.EMAIL_RICEVUTE
 WHERE THREAD_KEY = :p_thread
UNION ALL
SELECT 
    ID,
    TO_CLOB(UTENTE) AS MITTENTE,
    TO_CLOB(DESTINATARI) AS DESTINATARI,
    TO_CLOB(OGGETTO) AS OGGETTO,
    DATA_INVIO AS DATA,
    CORPO_HTML,
    CORPO_TESTO,
    MESSAGE_ID,
    IN_REPLY_TO,
    REFERENCES_HDR,
    THREAD_KEY,
    'I' AS TIPO
  FROM SGAPP.EMAIL_INVIATE
 WHERE THREAD_KEY = :p_thread
ORDER BY DATA";

            await using var cmd = new OracleCommand(sql, conn);
            cmd.Parameters.Add("p_thread", OracleDbType.Varchar2).Value = threadKey;

            var list = new List<EmailDetail_NEW>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new EmailDetail_NEW
                {
                    Id = reader.GetInt32(0),
                    Mittente = reader.IsDBNull(1) ? null : reader.GetString(1),
                    Destinatari = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Oggetto = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Data = reader.IsDBNull(4) ? null : reader.GetDateTime(4),
                    CorpoHtml = reader.IsDBNull(5) ? null : reader.GetString(5),
                    CorpoTesto = reader.IsDBNull(6) ? null : reader.GetString(6),
                    MessageId = reader.IsDBNull(7) ? null : reader.GetString(7),
                    InReplyTo = reader.IsDBNull(8) ? null : reader.GetString(8),
                    References = reader.IsDBNull(9) ? null : reader.GetString(9),
                    ThreadKey = reader.IsDBNull(10) ? null : reader.GetString(10),
                    Tipo = reader.GetString(11), // R = ricevuta, I = inviata
                    IsLoaded = true
                });
            }

            // 3️⃣ Recupera allegati per ogni messaggio
            const string attachSql = @"
        SELECT ID, NOME_FILE
        FROM SGAPP.EMAIL_ALLEGATI
        WHERE EMAIL_ID = :id_email";

            foreach (var mail in list)
            {
                mail.Allegati = new List<AllegatoItem_NEW>();
                await using var aCmd = new OracleCommand(attachSql, conn);
                aCmd.Parameters.Add("id_email", OracleDbType.Int32).Value = mail.Id;

                await using var aReader = await aCmd.ExecuteReaderAsync();
                while (await aReader.ReadAsync())
                {
                    mail.Allegati.Add(new AllegatoItem_NEW
                    {
                        Id = aReader.GetInt32(0),
                        NomeFile = aReader.IsDBNull(1) ? "" : aReader.GetString(1)
                    });
                }
            }

            return list;
        }


        public async Task AssignEmailAsync(
      int emailId,
      string utente,
      bool soloInvio,
      string? commento = null,
      string? eseguitoDa = null,
      CancellationToken ct = default)
        {
            await using var conn = new OracleConnection(_connectionString);
            await conn.OpenAsync(ct);

            await using var tx = conn.BeginTransaction();

            try
            {
                // 1) Leggo eventuali assegnatari attuali diversi dal nuovo
                const string sqlOldAssignees = @"
SELECT DISTINCT UTENTE
FROM SGAPP.EMAIL_ASSEGNAZIONI
WHERE EMAIL_ID = :p_eid
  AND UPPER(UTENTE) <> UPPER(:p_user)";

                var vecchiUtenti = new List<string>();

                await using (var cmdOld = new OracleCommand(sqlOldAssignees, conn))
                {
                    cmdOld.BindByName = true;
                    cmdOld.Transaction = tx;

                    cmdOld.Parameters.Add("p_eid", OracleDbType.Int32).Value = emailId;
                    cmdOld.Parameters.Add("p_user", OracleDbType.Varchar2).Value = utente;

                    await using var reader = await cmdOld.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        if (!reader.IsDBNull(0))
                            vecchiUtenti.Add(reader.GetString(0));
                    }
                }

                // 2) Archivia la mail per i vecchi assegnatari
                const string sqlInsertArchivio = @"
INSERT INTO SGAPP.EMAIL_ARCHIVIO (ID_EMAIL, UTENTE, DATA_ARCHIVIAZIONE)
SELECT :p_eid, :p_user, SYSDATE
FROM DUAL
WHERE NOT EXISTS (
    SELECT 1
    FROM SGAPP.EMAIL_ARCHIVIO
    WHERE ID_EMAIL = :p_eid_check
      AND UPPER(UTENTE) = UPPER(:p_user_check)
)";

                foreach (var vecchioUtente in vecchiUtenti)
                {
                    await using var cmdArch = new OracleCommand(sqlInsertArchivio, conn)
                    {
                        BindByName = true,
                        Transaction = tx
                    };

                    cmdArch.Parameters.Add("p_eid", OracleDbType.Int32).Value = emailId;
                    cmdArch.Parameters.Add("p_user", OracleDbType.Varchar2).Value = vecchioUtente;
                    cmdArch.Parameters.Add("p_eid_check", OracleDbType.Int32).Value = emailId;
                    cmdArch.Parameters.Add("p_user_check", OracleDbType.Varchar2).Value = vecchioUtente;

                    await cmdArch.ExecuteNonQueryAsync(ct);
                }

                // 3) Elimino le vecchie assegnazioni diverse dal nuovo utente
                const string sqlDeleteOldAssignments = @"
DELETE FROM SGAPP.EMAIL_ASSEGNAZIONI
WHERE EMAIL_ID = :p_eid
  AND UPPER(UTENTE) <> UPPER(:p_user)";

                await using (var cmdDel = new OracleCommand(sqlDeleteOldAssignments, conn))
                {
                    cmdDel.BindByName = true;
                    cmdDel.Transaction = tx;

                    cmdDel.Parameters.Add("p_eid", OracleDbType.Int32).Value = emailId;
                    cmdDel.Parameters.Add("p_user", OracleDbType.Varchar2).Value = utente;

                    await cmdDel.ExecuteNonQueryAsync(ct);
                }

                // 4) Se il nuovo assegnatario aveva già l'email archiviata, la tolgo dall'archivio
                const string sqlDeleteArchiveForNewAssignee = @"
DELETE FROM SGAPP.EMAIL_ARCHIVIO
WHERE ID_EMAIL = :p_eid
  AND UPPER(UTENTE) = UPPER(:p_user)";

                await using (var cmdUnarchive = new OracleCommand(sqlDeleteArchiveForNewAssignee, conn))
                {
                    cmdUnarchive.BindByName = true;
                    cmdUnarchive.Transaction = tx;

                    cmdUnarchive.Parameters.Add("p_eid", OracleDbType.Int32).Value = emailId;
                    cmdUnarchive.Parameters.Add("p_user", OracleDbType.Varchar2).Value = utente;

                    await cmdUnarchive.ExecuteNonQueryAsync(ct);
                }

                // 5) Inserisco la nuova assegnazione solo se non esiste già
                const string sqlAssegna = @"
INSERT INTO SGAPP.EMAIL_ASSEGNAZIONI (EMAIL_ID, UTENTE, SOLO_INVIO)
SELECT :p_eid, :p_user, :p_solo
FROM DUAL
WHERE NOT EXISTS (
    SELECT 1
    FROM SGAPP.EMAIL_ASSEGNAZIONI
    WHERE EMAIL_ID = :p_eid_check
      AND UPPER(UTENTE) = UPPER(:p_user_check)
)";

                await using (var cmd = new OracleCommand(sqlAssegna, conn))
                {
                    cmd.BindByName = true;
                    cmd.Transaction = tx;

                    cmd.Parameters.Add("p_eid", OracleDbType.Int32).Value = emailId;
                    cmd.Parameters.Add("p_user", OracleDbType.Varchar2).Value = utente;
                    cmd.Parameters.Add("p_solo", OracleDbType.Char).Value = soloInvio ? "Y" : "N";
                    cmd.Parameters.Add("p_eid_check", OracleDbType.Int32).Value = emailId;
                    cmd.Parameters.Add("p_user_check", OracleDbType.Varchar2).Value = utente;

                    await cmd.ExecuteNonQueryAsync(ct);
                }

                // 6) Commento manuale, se presente
                if (!string.IsNullOrWhiteSpace(commento))
                {
                    const string sqlCommento = @"
INSERT INTO SGAPP.COMMENTI_EMAIL (EMAIL_ID, AUTORE, TESTO, DATA_CREAZIONE)
VALUES (:p_eid, :p_autore, :p_testo, SYSDATE)";

                    await using var cmd2 = new OracleCommand(sqlCommento, conn)
                    {
                        BindByName = true,
                        Transaction = tx
                    };

                    cmd2.Parameters.Add("p_eid", OracleDbType.Int32).Value = emailId;
                    cmd2.Parameters.Add("p_autore", OracleDbType.Varchar2).Value = eseguitoDa ?? utente;
                    cmd2.Parameters.Add("p_testo", OracleDbType.Clob).Value = commento;

                    await cmd2.ExecuteNonQueryAsync(ct);
                }

                // 7) Activity automatica
                await AddAssignActivityAsync(
                    conn,
                    tx,
                    emailId,
                    eseguitoDa ?? "unknown",
                    utente,
                    ct
                );

                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }
        public async Task ArchiveEmailAsync(int emailId, string utente)
        {
            using var con = new OracleConnection(_connectionString);
            await con.OpenAsync();

            var cmd = con.CreateCommand();
            cmd.CommandText = @"
BEGIN
    INSERT INTO SGAPP.EMAIL_ARCHIVIO (ID_EMAIL, UTENTE, DATA_ARCHIVIAZIONE)
    VALUES (:emailId, :utente, SYSDATE);
EXCEPTION
    WHEN DUP_VAL_ON_INDEX THEN
        NULL; -- già archiviata: ignora
END;";

            cmd.Parameters.Add(new OracleParameter("emailId", emailId));
            cmd.Parameters.Add(new OracleParameter("utente", utente));

            await cmd.ExecuteNonQueryAsync();
        }






        public async Task<bool> IsArchivedAsync(int emailId, string utente)
        {
            using var con = new OracleConnection(_connectionString);
            await con.OpenAsync();

            var cmd = con.CreateCommand();
            cmd.CommandText = @"
        SELECT 1 FROM SGAPP.EMAIL_ARCHIVIO
        WHERE ID_EMAIL = :id AND UTENTE = :utente";

            cmd.Parameters.Add(new OracleParameter("id", emailId));
            cmd.Parameters.Add(new OracleParameter("utente", utente));

            var r = await cmd.ExecuteScalarAsync();
            return r != null;
        }



        private static string NormalizeSubject(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var x = s.Trim();
            var patterns = new[] { "RE:", "R:", "FWD:", "FW:", "I:" };
            bool changed;
            do
            {
                changed = false;
                var t = x.TrimStart();
                foreach (var p in patterns)
                    if (t.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                    {
                        t = t.Substring(p.Length).TrimStart(' ', '\t', ':');
                        changed = true;
                    }
                x = t;
            } while (changed);
            return x.ToUpperInvariant();
        }

        private static string MapFolderToUi(string dbFolder)
        {
            var norm = NormalizeFolder(dbFolder);

            if (norm == "INBOX" || norm.StartsWith("INBOX")) return "inbox";
            if (norm.Contains("POSTA INVIATA") || norm.Contains("SENT")) return "sent";
            if (norm.Contains("BOZZE") || norm.Contains("DRAFTS")) return "drafts";
            if (norm.Contains("SPAM") || norm.Contains("JUNK")) return "spam";
            if (norm.Contains("CESTINO") || norm.Contains("TRASH")) return "trash";
            return "custom:" + dbFolder;
        }

        private static async Task EnsureOpenAsync(IMailFolder folder, FolderAccess access, CancellationToken ct)
        {
            if (!folder.IsOpen || folder.Access != access)
                await folder.OpenAsync(access, ct);
        }

        private static async Task<List<IMailFolder>> GetAllFoldersAsync(ImapClient client, CancellationToken ct)
        {
            var list = new List<IMailFolder> { client.Inbox };
            try { var all = client.GetFolder(SpecialFolder.All); if (all != null) list.Add(all); } catch { }

            foreach (var ns in client.PersonalNamespaces)
            {
                IMailFolder root;
                try { root = client.GetFolder(ns); }
                catch { continue; }

                await GatherFoldersRec(root, list, ct);
            }
            return list.GroupBy(f => f.FullName).Select(g => g.First()).ToList();
        }

        private static async Task GatherFoldersRec(IMailFolder root, List<IMailFolder> acc, CancellationToken ct)
        {
            if ((root.Attributes & FolderAttributes.NonExistent) != 0) return;

            var ban = FolderAttributes.NonExistent;
            if ((root.Attributes & ban) == 0 && !acc.Any(x => x.FullName == root.FullName))
                acc.Add(root);

            try
            {
                foreach (var sub in await root.GetSubfoldersAsync(false, ct))
                    await GatherFoldersRec(sub, acc, ct);
            }
            catch { }
        }

        /* ================== CASELLA ================== */

        public async Task<int> GetCasellaIdAsync(string usernameOrEmail, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(usernameOrEmail))
                throw new ArgumentException("Username o Email mancante.", nameof(usernameOrEmail));

            var key = usernameOrEmail.Trim().ToUpperInvariant();

            await using var db = _dbFactory.CreateDbContext();

            if (key.Contains("@"))
            {
                var idEmail = await db.CasellePosta
                    .AsNoTracking()
                    .Where(c => (c.Email ?? "").Trim().ToUpper() == key)
                    .Select(c => c.Id)
                    .FirstOrDefaultAsync(ct);

                if (idEmail != 0)
                    return idEmail;
            }

            var idUtente = await db.CasellaAbilitazioni
                .AsNoTracking()
                .Where(a => (a.Username ?? "").Trim().ToUpper() == key)
                .Select(a => a.CasellaId)
                .FirstOrDefaultAsync(ct);

            if (idUtente != 0)
                return idUtente;
            throw new InvalidOperationException($"Nessuna casella associata a '{usernameOrEmail}'.");
        }
        private static readonly Regex UsernamePattern =
    new(@"^[a-z]\.[a-z]+$", RegexOptions.IgnoreCase);
        public async Task<List<string>> GetUserListAsync()
        {
            var list = new List<string>();

            await using var conn = await GetOpenConnectionAsync();

            const string sql = @"
        SELECT LOWER(UTENTE)
        FROM INFOUSER
";

            await using var cmd = new OracleCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
                list.Add(reader.GetString(0));

            return list;
        }



        /* ================== FOLDER SIDEBAR ================== */

        public async Task<List<FolderItem_NEW>> GetFoldersAsync(List<int> casellaIds, CancellationToken ct = default)
{
    await using var db = _dbFactory.CreateDbContext();

    // 🟢 Filtra tutte le email delle caselle specificate
    var rawCounts = await db.EmailRicevute
        .AsNoTracking()
        .Where(e => casellaIds.Contains(e.CasellaId))
        .GroupBy(e => e.FolderPath)
        .Select(g => new { Folder = g.Key, Cnt = g.Count() })
        .ToListAsync(ct);

    var perUi = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    foreach (var r in rawCounts)
    {
        if (string.IsNullOrWhiteSpace(r.Folder)) continue;
        var ui = MapFolderToUi(r.Folder!);
        if (!perUi.ContainsKey(ui)) perUi[ui] = 0;
        perUi[ui] += r.Cnt;
    }

    var important = new List<FolderItem_NEW>
    {
        new("inbox",  "Posta in arrivo", "bi-inbox-fill",           0, perUi.TryGetValue("inbox", out var c0) ? c0 : 0),
        new("all",    "Tutti",           "bi-collection-fill",      1, perUi.Values.Sum()),
        new("sent",   "Inviati",         "bi-send-fill",            2, perUi.TryGetValue("sent", out var c1) ? c1 : 0),
        new("drafts", "Bozze",           "bi-file-earmark-text",    3, perUi.TryGetValue("drafts", out var c2) ? c2 : 0),
        new("spam",   "Spam",            "bi-exclamation-octagon",  4, perUi.TryGetValue("spam", out var c3) ? c3 : 0),
        new("trash",  "Cestino",         "bi-trash3-fill",          5, perUi.TryGetValue("trash", out var c4) ? c4 : 0)
    };

    // migliora "Tutti": senza spam/trash
    var withoutTrashSpam = perUi.Where(kv => kv.Key != "trash" && kv.Key != "spam").Sum(kv => kv.Value);
    var idxAll = important.FindIndex(f => f.UiName == "all");
    if (idxAll >= 0)
        important[idxAll] = important[idxAll] with { Count = withoutTrashSpam };

    return important;
}


        /* ================== LISTA EMAIL (VIRTUALIZE) ================== */

        
        // 🔹 OVERLOAD: carica email da più caselle contemporaneamente
        public async Task<(List<EmailListItem_NEW>, int)> GetEmailPageAsync(
     List<int> casellaIds,
     int start,
     int pageSize,
     string folderUi,
     string utente,
     string? filtro)
        {
            using var conn = new OracleConnection(_connectionString);
            await conn.OpenAsync();

            Console.WriteLine($"🔎 DEBUG filtro='{filtro}' folder={folderUi} start={start}");

            // ========== LISTA ==========
            string baseSql = @"
SELECT 
    e.ID,
    e.MITTENTE,
    e.DESTINATARI,
    e.OGGETTO,
    e.DATA_RICEZIONE,
    e.CASELLA_ID,
    e.FOLDER_PATH,
    c.EMAIL as CASELLA_EMAIL,
    e.APERTO,
    e.CC,
    e.CCN,
    CASE WHEN EXISTS (
        SELECT 1 FROM SGAPP.EMAIL_ALLEGATI a WHERE a.EMAIL_ID = e.ID
    ) THEN 1 ELSE 0 END AS HAS_ATTACH,
   (
    SELECT LISTAGG(
        a.ID || '::' || a.NOME_FILE || '::' || NVL(a.MIME_TYPE,''),
        '||'
    ) WITHIN GROUP (ORDER BY a.ID)
    FROM SGAPP.EMAIL_ALLEGATI a
    WHERE a.EMAIL_ID = e.ID
    ) AS ATT_PACK,
    SUBSTR(NVL(e.CORPO_TESTO, e.CORPO_HTML), 1, 200) AS PREVIEW,
    (
        SELECT LISTAGG(x.UTENTE, ';') WITHIN GROUP (ORDER BY x.ID)
        FROM SGAPP.EMAIL_ASSEGNAZIONI x
        WHERE x.EMAIL_ID = e.ID
    ) AS ASSEGNATO_A
FROM SGAPP.EMAIL_RICEVUTE e
JOIN SGAPP.CASELLEPOSTA c ON c.ID = e.CASELLA_ID
WHERE 1=1";

            // --- filtro per cartella logica ---
            if (folderUi == "inbox")
            {
                baseSql += @"
        AND EXISTS (
            SELECT 1 
            FROM SGAPP.EMAIL_ASSEGNAZIONI x 
            WHERE x.EMAIL_ID = e.ID 
              AND x.UTENTE = :utente
        )
        AND NOT EXISTS (
            SELECT 1
            FROM SGAPP.EMAIL_ARCHIVIO ar
            WHERE ar.ID_EMAIL = e.ID
              AND ar.UTENTE = :utente
        ) 
        AND NOT EXISTS (
            SELECT 1
            FROM SGAPP.EMAIL_INBOX_SEZIONE_MAP m
            WHERE m.ID_EMAIL = e.ID
              AND m.UTENTE = :utente
        )";

            }

            else if (folderUi == "myarchive")
            {
                baseSql += @"
                    AND EXISTS (
                        SELECT 1 
                        FROM SGAPP.EMAIL_ARCHIVIO a 
                        WHERE a.ID_EMAIL = e.ID 
                          AND a.UTENTE = :utente
                    )";
            }

            else if (folderUi == "all")
            {
                baseSql += " AND e.CASELLA_ID IN (" + string.Join(",", casellaIds) + ")";
            }

            // --- filtro di ricerca (SOLO header + nome allegato) ---
            if (!string.IsNullOrWhiteSpace(filtro))
            {
                baseSql += @"
                AND (
                      LOWER(NVL(e.OGGETTO,''))      LIKE :filtro
                   OR LOWER(NVL(e.MITTENTE,''))     LIKE :filtro
                   OR LOWER(NVL(e.DESTINATARI,''))  LIKE :filtro
                   OR LOWER(NVL(e.CC,''))           LIKE :filtro
                   OR LOWER(NVL(e.CCN,''))          LIKE :filtro
                   OR EXISTS (
                        SELECT 1
                        FROM SGAPP.EMAIL_ALLEGATI a
                        WHERE a.EMAIL_ID = e.ID
                          AND LOWER(NVL(a.NOME_FILE,'')) LIKE :filtro
                   )
                )";
            }

            // --- ordinamento e paginazione ---
            baseSql += @"
ORDER BY e.DATA_RICEZIONE DESC
OFFSET :p_start ROWS FETCH NEXT :p_pageSize ROWS ONLY";

            await using var cmd = new OracleCommand(baseSql, conn) { BindByName = true };

            if (folderUi == "inbox" || folderUi == "myarchive")
                cmd.Parameters.Add("utente", OracleDbType.Varchar2).Value = utente;

            if (!string.IsNullOrWhiteSpace(filtro))
                cmd.Parameters.Add("filtro", OracleDbType.Varchar2).Value = $"%{filtro!.ToLower()}%";

            cmd.Parameters.Add("p_start", OracleDbType.Int32).Value = start;
            cmd.Parameters.Add("p_pageSize", OracleDbType.Int32).Value = pageSize;

            var list = new List<EmailListItem_NEW>();
            await using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    string? attPack = reader.IsDBNull("ATT_PACK") ? null : reader.GetString("ATT_PACK");

                    List<AllegatoItem_NEW>? allegati = null;
                    if (!string.IsNullOrWhiteSpace(attPack))
                    {
                        allegati = attPack
                            .Split("||", StringSplitOptions.RemoveEmptyEntries)
                            .Select(x =>
                            {
                                var parts = x.Split("::");
                                var id = int.Parse(parts[0]);
                                var nome = parts.Length > 1 ? parts[1] : "";
                                var mime = parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]) ? parts[2] : null;
                                return new AllegatoItem_NEW(id, nome, mime);
                            })
                            .ToList();
                    }



                    list.Add(new EmailListItem_NEW(
                        Id: reader.GetInt32("ID"),
                        Data: reader.IsDBNull("DATA_RICEZIONE") ? (DateTime?)null : reader.GetDateTime("DATA_RICEZIONE"),
                        Mittente: reader.GetString("MITTENTE"),
                        Oggetto: reader.GetString("OGGETTO"),
                        Aperto: reader.IsDBNull("APERTO") ? "" : reader.GetString("APERTO"),
                        HasAttachments: reader.GetInt32("HAS_ATTACH") == 1,
                        ThreadLen: 1,
                        Replies: 0,
                        Preview: reader.IsDBNull("PREVIEW") ? null : reader.GetString("PREVIEW"),
                        Allegati: allegati,
                        MessageId: null,
                        CasellaId: reader.GetInt32("CASELLA_ID"),
                        CasellaEmail: reader.IsDBNull("CASELLA_EMAIL") ? null : reader.GetString("CASELLA_EMAIL"),
                        AssegnatoA: reader.IsDBNull("ASSEGNATO_A") ? null : reader.GetString("ASSEGNATO_A"),
                        Destinatari: GetStr(reader, "DESTINATARI"),
                        Cc: GetStr(reader, "CC"),
                        Ccn: GetStr(reader, "CCN")
                    ));
                }
            }

            // ========== COUNT ==========
            string countSql = @"
SELECT COUNT(*)
FROM SGAPP.EMAIL_RICEVUTE e
WHERE 1=1";

            if (folderUi == "inbox")
            {
                baseSql += @"
                            AND EXISTS (
                                SELECT 1 
                                FROM SGAPP.EMAIL_ASSEGNAZIONI x 
                                WHERE x.EMAIL_ID = e.ID 
                                  AND x.UTENTE = :utente
                            )
                            AND NOT EXISTS (
                                SELECT 1
                                FROM SGAPP.EMAIL_ARCHIVIO ar
                                WHERE ar.ID_EMAIL = e.ID
                                  AND ar.UTENTE = :utente
                            )";
            }

            else if (folderUi == "myarchive")
            {
                countSql += @"
                    AND EXISTS (
                        SELECT 1 
                        FROM SGAPP.EMAIL_ARCHIVIO a 
                        WHERE a.ID_EMAIL = e.ID 
                          AND a.UTENTE = :utente
                    )";
            }

            else if (folderUi == "all")
            {
                countSql += " AND e.CASELLA_ID IN (" + string.Join(",", casellaIds) + ")";
            }

            if (!string.IsNullOrWhiteSpace(filtro))
            {
                countSql += @"
        AND (
              LOWER(NVL(e.OGGETTO,''))       LIKE :filtro
           OR LOWER(NVL(e.MITTENTE,''))      LIKE :filtro
           OR LOWER(NVL(e.DESTINATARI,''))   LIKE :filtro
           OR LOWER(NVL(e.CORPO_TESTO,''))   LIKE :filtro
           OR LOWER(NVL(e.CORPO_HTML,''))    LIKE :filtro
        )";
            }

            await using var countCmd = new OracleCommand(countSql, conn) { BindByName = true };
            if (folderUi == "inbox" || folderUi == "myarchive")
                countCmd.Parameters.Add("utente", OracleDbType.Varchar2).Value = utente;
            if (!string.IsNullOrWhiteSpace(filtro))
                countCmd.Parameters.Add("filtro", OracleDbType.Varchar2).Value = $"%{filtro!.ToLower()}%";

            int total = Convert.ToInt32(await countCmd.ExecuteScalarAsync());

            return (list, total);
        }

        static string? GetStr(OracleDataReader r, string col)
    => r.IsDBNull(col) ? null : r.GetString(col);
        public async Task SaveSentAsync(
    string utente,
    string destinatari,
    string? oggetto,
    string? corpoHtml,
    string? corpoTesto,  // 👈 aggiunto qui
    List<OutgoingAttachment_NEW>? allegati = null,
    CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();

            var email = new EmailInviata
            {
                CasellaId = await GetCasellaIdAsync(utente, ct),
                Utente = utente,
                Destinatari = destinatari,
                Oggetto = oggetto,
                CorpoHtml = corpoHtml ?? string.Empty,
                CorpoTesto = corpoTesto ?? string.Empty,  // 👈 ora è validoA
                DataInvio = DateTime.Now
            };

            db.EmailInviate.Add(email);
            await db.SaveChangesAsync(ct);

            if (allegati != null && allegati.Any())
            {
                foreach (var a in allegati)
                {
                    db.InviataAllegati.Add(new InviataAllegato
                    {
                        EmailId = email.Id,
                        NomeFile = a.FileName,
                        MimeType = a.MimeType,
                        Content = a.Content
                    });
                }
                await db.SaveChangesAsync(ct);
            }
        }
        // --- helper una sola volta nella classe -----------------------
        private static readonly string[] SentKeywords =
        {
    "SENT",                // generico
    "POSTA INVIATA",       // italiano
    "INVIATI", "INVIATE",  // varianti
    "INBOX.SENT"           // qualcuno salva così
};


        private static bool IsSentFolder(string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return false;
            foreach (var kw in SentKeywords)
                if (folder.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;

            // Varianti tipiche di Gmail (etichetta localizzata)
            // [GMAIL]/POSTA INVIATA  |  [Gmail]/Posta inviata
            if (folder.IndexOf("[GMAIL]/POSTA INVIATA", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (folder.IndexOf("[Gmail]/Posta inviata", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            return false;
        }
        // ---------------------------------------------------------------
        class EmailRowTmp
        {
            public int Id { get; set; }
            public DateTime? Data { get; set; }
            public string Mittente { get; set; } = "";
            public string? Oggetto { get; set; }
            public string? Aperto { get; set; }
            public string? CorpoHtml { get; set; }
            public string? CorpoTesto { get; set; }
            public List<AllegatoItem_NEW> Allegati { get; set; } = new();
        }
        





        /* ================== DETTAGLIO & CONVERSAZIONE ================== */

        public async Task<EmailDetail_NEW?> GetEmailDetailAsync(int id, bool onlyMeta, CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();
            var q = db.EmailRicevute.AsNoTracking().Where(e => e.Id == id);

            if (onlyMeta)
            {
                var dto = await q.Select(e => new
                {
                    e.Id,
                    e.Mittente,
                    e.Destinatari,
                    e.Oggetto,
                    e.DataRicezione,
                    e.Aperto
                }).FirstOrDefaultAsync(ct);

                if (dto == null) return null;

                return new EmailDetail_NEW
                {
                    Id = dto.Id,
                    Mittente = dto.Mittente,
                    Destinatari = dto.Destinatari,
                    Oggetto = dto.Oggetto,
                    Data = dto.DataRicezione,
                    Aperto = dto.Aperto,
                    IsLoaded = false
                };
            }
            else
            {
                var dto = await q.Select(e => new
                {
                    e.Id,
                    e.Mittente,
                    e.Destinatari,
                    e.Oggetto,
                    e.DataRicezione,
                    e.CorpoHtml,
                    e.CorpoTesto,
                    e.Aperto,
                    e.MessageId,
                    e.InReplyTo,
                    e.ReferencesHdr,     // 🟢 nome del campo nel DB
                    e.ThreadKey,         // 🟢 nome del campo nel DB
                    Allegati = e.Allegati
                        .Select(a => new AllegatoItem_NEW(a.Id, a.NomeFile, a.MimeType))
                        .ToList()
                }).FirstOrDefaultAsync(ct);

                if (dto == null) return null;

                return new EmailDetail_NEW
                {
                    Id = dto.Id,
                    Mittente = dto.Mittente,
                    Destinatari = dto.Destinatari,
                    Oggetto = dto.Oggetto,
                    Data = dto.DataRicezione,
                    CorpoHtml = dto.CorpoHtml,
                    CorpoTesto = dto.CorpoTesto,
                    Allegati = dto.Allegati,
                    Aperto = dto.Aperto,
                    MessageId = dto.MessageId,
                    InReplyTo = dto.InReplyTo,
                    References = dto.ReferencesHdr,
                    ThreadKey = dto.ThreadKey,
                    IsLoaded = true
                };
            }
        }


        public record DraftListItem(
            long Id,
            string? Oggetto,
            DateTime? LastSaved,
            string? Destinatari,
            string? Cc,
            string? Ccn,
            string? Preview,
            bool Letto
        );

        public async Task<(List<DraftListItem> Page, int Total)> GetDraftsPagedAsync(
    string utente,
    int start,
    int pageSize,
    string? searchText = null)
        {
            await using var db = _dbFactory.CreateDbContext();

            var q = db.EmailBozze
                .Where(x => x.Utente == utente);

            if (!string.IsNullOrWhiteSpace(searchText))
            {
                var s = searchText.Trim();
                q = q.Where(x =>
                    (x.Oggetto ?? "").Contains(s) ||
                    (x.Destinatari ?? "").Contains(s) ||
                    (x.Cc ?? "").Contains(s) ||
                    (x.Ccn ?? "").Contains(s));
            }

            var total = await q.CountAsync();

            var page = await q
                .OrderByDescending(x => x.LastSaved)
                .Skip(start)
                .Take(pageSize)
                .Select(x => new DraftListItem(
                    x.Id,
                    x.Oggetto,
                    x.LastSaved,
                    x.Destinatari,
                    x.Cc,
                    x.Ccn,
                    
                    // preview: primi 140 char del body (html ripulito minimo)
                    (x.CorpoHtml ?? "").Length > 140 ? (x.CorpoHtml ?? "").Substring(0, 140) : (x.CorpoHtml ?? ""),
                    x.Letto
                ))
                .ToListAsync();

            return (page, total);
        }
        public async Task<EmailBozza?> GetDraftByIdAsync(long id, string utente)
        {
            await using var db = _dbFactory.CreateDbContext();
            return await db.EmailBozze.FirstOrDefaultAsync(x => x.Id == id && x.Utente == utente);
        }

        public async Task DeleteDraftAsync(long id, string utente)
        {
            await using var db = _dbFactory.CreateDbContext();
            var d = await db.EmailBozze.FirstOrDefaultAsync(x => x.Id == id && x.Utente == utente);
            if (d == null) return;
            db.EmailBozze.Remove(d);
            await db.SaveChangesAsync();
        }
        public async Task<long?> SaveDraftAsync(
     string utente,
     string to,
     string cc,
     string ccn,
     string? subject,
     string bodyHtml,
     long? draftId)
        {
            using var db = _dbFactory.CreateDbContext();

            EmailBozza? bozza = null;

            // 1) Se mi dai un draftId provo a ricaricarla
            if (draftId.HasValue)
            {
                bozza = await db.EmailBozze
                    .FirstOrDefaultAsync(x => x.Id == draftId.Value && x.Utente == utente);
            }

            // 2) Se non esiste, la creo
            if (bozza == null)
            {
                bozza = new EmailBozza
                {
                    Utente = utente
                };

                db.EmailBozze.Add(bozza);
            }

            // 3) Aggiorno i campi
            bozza.Destinatari = to;
            bozza.Cc = cc;
            bozza.Ccn = ccn;
            bozza.Oggetto = subject;
            bozza.CorpoHtml = bodyHtml;
            bozza.Letto = false;
            // ✅ OBBLIGATORIO: colonna NOT NULL in Oracle
            bozza.LastSaved = DateTime.Now;

            await db.SaveChangesAsync();
            return bozza.Id;
        }




        public async Task<List<EmailBozza>> GetDraftsAsync(string utente, CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();
            return await db.EmailBozze
                .AsNoTracking()
                .Include(b => b.Allegati)
                .Where(b => b.Utente == utente)
                .OrderByDescending(b => b.LastSaved)
                .ToListAsync(ct);
        }


        public async Task<List<EmailDetail_NEW>> GetConversationAsync(int emailId, CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();

            var seed = await db.EmailRicevute
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == emailId, ct);

            if (seed is null) return new List<EmailDetail_NEW>();

            // 1) prendo tutti i messaggi della stessa casella (limite sicurezza)
            var all = await db.EmailRicevute
                .AsNoTracking()
                .Where(x => x.CasellaId == seed.CasellaId && (x.Eliminato == null || x.Eliminato != "Y"))
                .Select(x => new {
                    x.Id,
                    x.MessageId,
                    x.InReplyTo,
                    x.ReferencesHdr,
                    x.Mittente,
                    x.Destinatari,
                    x.Oggetto,
                    x.DataRicezione,
                    x.Aperto
                })
                .ToListAsync(ct);

            // 2) espando per ID messaggio (closure su In-Reply-To/References)
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void AddId(string? s) { if (!string.IsNullOrWhiteSpace(s)) ids.Add(s.Trim()); }

            AddId(seed.MessageId);
            bool changed;
            do
            {
                changed = false;
                foreach (var m in all)
                {
                    // references può contenere più id
                    var refs = (m.ReferencesHdr ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);

                    // match se uno qualunque combacia con l'insieme
                    bool linked =
                        ids.Contains(m.MessageId ?? "") ||
                        ids.Contains(m.InReplyTo ?? "") ||
                        refs.Any(r => ids.Contains(r));

                    // oppure se questo messaggio punta a uno degli ID noti
                    linked = linked ||
                             (!string.IsNullOrWhiteSpace(m.InReplyTo) && ids.Contains(m.InReplyTo)) ||
                             refs.Any(r => ids.Contains(r));

                    if (linked)
                    {
                        int before = ids.Count;
                        AddId(m.MessageId);
                        AddId(m.InReplyTo);
                        foreach (var r in refs) AddId(r);
                        if (ids.Count > before) changed = true;
                    }
                }
            } while (changed);

            // 3) seleziono solo i messaggi coinvolti e li ordino
            var threadRows = all
                .Where(m =>
                    ids.Contains(m.MessageId ?? "") ||
                    ids.Contains(m.InReplyTo ?? "") ||
                    ((m.ReferencesHdr ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(ids.Contains)))
                .OrderBy(m => m.DataRicezione)
                .ToList();

            // 4) ritorno DTO "leggeri"; il corpo lo cariciamo solo quando serve
            var conv = threadRows.Select(m => new EmailDetail_NEW
            {
                Id = m.Id,
                Mittente = m.Mittente,
                Destinatari = m.Destinatari,
                Oggetto = m.Oggetto,
                Data = m.DataRicezione,
                Aperto = m.Aperto,
                IsLoaded = false,
                CorpoHtml = null,
                CorpoTesto = null,
                Allegati = new List<AllegatoItem_NEW>()
            }).ToList();

            // garanzia: se per qualunque motivo è vuoto, metto la mail corrente
            if (conv.Count == 0)
            {
                conv.Add(new EmailDetail_NEW
                {
                    Id = seed.Id,
                    Mittente = seed.Mittente,
                    Destinatari = seed.Destinatari,
                    Oggetto = seed.Oggetto,
                    Data = seed.DataRicezione,
                    Aperto = seed.Aperto,
                    IsLoaded = false,
                    Allegati = new List<AllegatoItem_NEW>()
                });
            }

            return conv;
        }



        /* ================== STATE MUTATIONS ================== */

        public async Task MarkOpenedAsync(int emailId, CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();
            var e = await db.EmailRicevute.FirstOrDefaultAsync(x => x.Id == emailId, ct);
            if (e != null && e.Aperto != "Y")
            {
                e.Aperto = "Y";
                await db.SaveChangesAsync(ct);
            }
        }

        public async Task MoveToTrashAsync(int emailId, CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();
            var e = await db.EmailRicevute.FirstOrDefaultAsync(x => x.Id == emailId, ct);
            if (e != null)
            {
                e.FolderPath = "[GMAIL]/Trash"; // adegua al tuo server
                await db.SaveChangesAsync(ct);
            }
        }



        /* ================== SEND (con allegati + append opzionale in Sent) ================== */

        public record OutgoingAttachment_NEW(string FileName, string? MimeType, byte[] Content);

        public async Task MarkUnreadAsync(int emailId, CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();
            var e = await db.EmailRicevute.FirstOrDefaultAsync(x => x.Id == emailId, ct);
            if (e != null && e.Aperto != "N")
            {
                e.Aperto = "N";
                await db.SaveChangesAsync(ct);
            }
        }

        public async Task<string> GetFirmaUtenteAsync(string username, CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();

            // Step 1: trova l’abilitazione
            var abilitazione = await db.CasellaAbilitazioni
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Username == username, ct);

            if (abilitazione == null)
                throw new Exception($"L'utente {username} non ha nessuna casella abilitata.");

            // Step 2: trova la casella email
            var casella = await db.CasellePosta
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == abilitazione.CasellaId, ct);

            if (casella == null)
                throw new Exception($"Nessuna casella trovata con ID {abilitazione.CasellaId}.");

            // Usa i dati della firma personalizzata (dall’abilitazione)
            var nome = abilitazione.Nome?.Trim() ?? "";
            var titolo = abilitazione.Titolo?.Trim() ?? "";
            var recapito = abilitazione.Recapito?.Trim() ?? "";

            // Stile base uniforme
            var styleBase = "font-family:'Segoe UI', Arial, sans-serif; font-size:13px; color:#000000;";
            var smallStyle = "font-size:12px; color:#333333;";
            var corporateStyle = "font-size:13px; font-style:italic; font-weight:bold; color:#004080;"; // blu scuro

            // Costruzione base
            var firma = $@"
<div style='{styleBase}'>
Ciao.<br/><br/>
<b>{System.Net.WebUtility.HtmlEncode(nome)}</b><br/>
{(string.IsNullOrWhiteSpace(titolo) ? "" : $"{System.Net.WebUtility.HtmlEncode(titolo)}<br/>")}
<small style='{smallStyle}'>{System.Net.WebUtility.HtmlEncode(recapito)}</small><br/><br/>
";

            // Firma aziendale dinamica in corsivo/blu
            if (casella.Email.EndsWith("@grupposantacroce.com", StringComparison.OrdinalIgnoreCase))
            {
                firma += $"<span style='{corporateStyle}'>GRUPPO SANTACROCE</span>";
            }
            else if (casella.Email.EndsWith("@rayaitaly.com", StringComparison.OrdinalIgnoreCase))
            {
                firma += $@"<span style='{corporateStyle}'>RAYA S.p.A. - Grains Commodities & Investment</span><br/>
<small style='{smallStyle}'>
Via San Raffaele n. 1 - 20121 MILANO - ITALY - V.A.T No.: IT 04306280712<br/>
This message and any attachments are confidential ...
</small>";
            }
            else if (casella.Email.EndsWith("@eurocereali.com", StringComparison.OrdinalIgnoreCase))
            {
                firma += $@"<span style='{corporateStyle}'>EUROCEREALI S.R.L. - Grains Commodities & Investment</span><br/>
<small style='{smallStyle}'>
Via V. Veneto n. 54B – 00187 ROMA - ITALY - V.A.T No.: IT 01381820537<br/>
This message and any attachments are confidential ...
</small>";
            }

            firma += "<br/><hr style='border:none; border-top:1px solid #cccccc; margin-top:6px;'/></div>";

            return firma;
        }


        private async Task<CasellaPosta?> GetCasellaAsync(int casellaId, CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();

            return await db.CasellePosta
                .Where(c => c.Id == casellaId)
                .Select(c => new CasellaPosta
                {
                    Id = c.Id,
                    Email = c.Email,
                    Password = c.Password,
                    Provider = c.Provider,
                    ImapHost = c.ImapHost,
                    ImapPort = c.ImapPort,
                    UseSsl = c.UseSsl,
                    CreatedAt = c.CreatedAt
                })
                .FirstOrDefaultAsync(ct);
        }

        public async Task SendEmailAsync(
    string usernameOrEmail,
    string to,
    string? cc = null,
    string? bcc = null,
    string subject = "",
    string bodyHtml = "",
    List<OutgoingAttachment_NEW>? attachments = null,
    string? fromAddressOverride = null,
    string? inReplyTo = null,
    string? referencesHdr = null,
    CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();

            // 1️⃣ Casella
            CasellaPosta? casella;
            if (!string.IsNullOrWhiteSpace(fromAddressOverride))
            {
                casella = await db.CasellePosta.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Email == fromAddressOverride, ct);
                if (casella == null)
                    throw new InvalidOperationException($"Nessuna casella trovata per {fromAddressOverride}");
            }
            else
            {
                var casellaId = await GetCasellaIdAsync(usernameOrEmail, ct);
                casella = await GetCasellaAsync(casellaId, ct)
                    ?? throw new InvalidOperationException($"Casella {casellaId} non trovata");
            }

            // 2️⃣ Nome mittente
            var abilitazione = await db.CasellaAbilitazioni
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.CasellaId == casella.Id && a.Username == usernameOrEmail, ct);

            var nomeMittente = string.IsNullOrWhiteSpace(abilitazione?.Nome)
                ? casella.Email
                : abilitazione!.Nome;

            // 3️⃣ Messaggio
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(nomeMittente, casella.Email));

            // Supporta più destinatari separati da ; o ,
            foreach (var addr in to.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
                message.To.Add(MailboxAddress.Parse(addr.Trim()));
            if (!string.IsNullOrWhiteSpace(cc))
            {
                foreach (var addr in cc.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
                    message.Cc.Add(MailboxAddress.Parse(addr.Trim()));
            }

            // BCC
            if (!string.IsNullOrWhiteSpace(bcc))
            {
                foreach (var addr in bcc.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
                    message.Bcc.Add(MailboxAddress.Parse(addr.Trim()));
            }
            message.Subject = subject ?? "(nessun oggetto)";

            var builder = new BodyBuilder { HtmlBody = bodyHtml ?? "" };
            if (attachments is { Count: > 0 })
            {
                foreach (var a in attachments)
                {
                    _logger.LogInformation(
                        "📎 Allegato: {Name} mime='{Mime}' bytes={Len}",
                        a.FileName,
                        a.MimeType,
                        a.Content?.Length ?? 0
                    );

                    var contentType = ToContentTypeSafe(a.MimeType, _logger, a.FileName);

                    builder.Attachments.Add(
                        a.FileName,
                        a.Content,                 // ✅ byte[]
                        contentType               // ✅ ContentType valido
                    );
                }
            }


            message.Body = builder.ToMessageBody();

            // ✅ 4️⃣ Header di threading (fondamentale)
            if (!string.IsNullOrWhiteSpace(inReplyTo))
            {
                var clean = inReplyTo.Trim('<', '>', ' ', '\t', '\r', '\n');
                message.InReplyTo = $"<{clean}>";
            }
            if (!string.IsNullOrWhiteSpace(referencesHdr))
            {
                var refs = referencesHdr
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(r => $"<{r.Trim('<', '>', ' ', '\t', '\r', '\n')}>")
                    .Distinct()
                    .ToList();

                if (!string.IsNullOrWhiteSpace(message.InReplyTo) && !refs.Contains(message.InReplyTo))
                    refs.Add(message.InReplyTo);

                foreach (var r in refs) message.References.Add(r);
            }

            var messageIdClean = (message.MessageId ?? "").Trim('<', '>', ' ', '\t', '\r', '\n');
            // 🔹 Calcolo THREAD_KEY coerente con email ricevute/inviate
            string threadKeyClean;

            if (!string.IsNullOrEmpty(inReplyTo))
            {
                var normalizedInReply = inReplyTo.Trim('<', '>', ' ', '\t', '\r', '\n').ToUpperInvariant();

                // Cerca se già esiste nel DB (EMAIL_RICEVUTE o EMAIL_INVIATE)
                var existingKey = await db.EmailInviate
                    .Where(e => e.MessageId.ToUpper() == normalizedInReply)
                    .Select(e => e.ThreadKey)
                    .FirstOrDefaultAsync(ct);

                if (string.IsNullOrEmpty(existingKey))
                {
                    existingKey = await db.EmailRicevute
                        .Where(e => e.MessageId.ToUpper() == normalizedInReply)
                        .Select(e => e.ThreadKey)
                        .FirstOrDefaultAsync(ct);
                }

                // Se trovata, eredito la chiave del thread esistente
                threadKeyClean = !string.IsNullOrEmpty(existingKey)
                    ? existingKey
                    : normalizedInReply;

                _logger.LogInformation("🧩 ThreadKey derivata da InReplyTo: {Key}", threadKeyClean);
            }
            else
            {
                // Nuovo thread → uso MessageId
                threadKeyClean = messageIdClean.ToUpperInvariant();
                _logger.LogInformation("🆕 Nuovo thread creato: {Key}", threadKeyClean);
            }

            var inReplyToClean = string.IsNullOrWhiteSpace(message.InReplyTo) ? null
                                : message.InReplyTo.Trim('<', '>', ' ', '\t', '\r', '\n');
            
            var corpoTesto = Regex.Replace(bodyHtml ?? "", "<.*?>", string.Empty);

            var inviata = new EmailInviata
            {
                CasellaId = casella.Id,
                Utente = usernameOrEmail,
                Destinatari = to,
                Cc = cc,
                Bcc = bcc,
                Oggetto = subject ?? "(nessun oggetto)",
                CorpoHtml = bodyHtml ?? "",
                CorpoTesto = corpoTesto,
                DataInvio = DateTime.UtcNow,
                MessageId = messageIdClean,
                InReplyTo = inReplyToClean,
                ReferencesHdr = string.Join(" ", message.References.Select(r => r.Trim('<', '>', ' ', '\t', '\r', '\n'))),
                ThreadKey = threadKeyClean
            };


            // Allegati
            if (attachments != null && attachments.Any())
            {
                foreach (var a in attachments)
                {
                    inviata.Allegati.Add(new InviataAllegato
                    {
                        NomeFile = a.FileName,
                        MimeType = a.MimeType ?? "application/octet-stream",
                        Content = a.Content,
                        Path = "" 
                    });
                }
            }

            db.EmailInviate.Add(inviata);
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("💾 Email inviata salvata ID={Id}, MessageId={MsgId}, ThreadKey={ThreadKey}",
                inviata.Id, inviata.MessageId, inviata.ThreadKey);
            if (inviata.Allegati is { Count: > 0 })
            {
                var basePath = _attachmentsOpt.Value.BasePath;
                if (string.IsNullOrWhiteSpace(basePath))
                    throw new InvalidOperationException("Attachments:BasePath non configurato");

                foreach (var al in inviata.Allegati)
                {
                    // usa il NomeFile + Content salvati nel DB entity
                    var rel = await SaveSentAttachmentAsync(
                        basePath: basePath,
                        casellaId: casella.Id,
                        emailId: inviata.Id,
                        originalFileName: al.NomeFile,
                        bytes: al.Content,
                        ct: ct
                    );

                    al.Path = rel;
                }

                await db.SaveChangesAsync(ct);

                _logger.LogInformation("📁 Allegati inviati salvati su FS per EmailId={Id} CasellaId={CasellaId}",
                    inviata.Id, casella.Id);
            }

            // 6️⃣ SMTP
            string smtpHost; int smtpPort; SecureSocketOptions socketOptions;
            if (casella.Provider.Equals("Gmail", StringComparison.OrdinalIgnoreCase))
                (smtpHost, smtpPort, socketOptions) = ("smtp.gmail.com", 587, SecureSocketOptions.StartTls);
            else if (casella.Provider.Equals("Aruba", StringComparison.OrdinalIgnoreCase))
            {
                if (casella.Email.Contains("@pec.", StringComparison.OrdinalIgnoreCase))
                {
                    // PEC Aruba
                    smtpHost = "smtps.pec.aruba.it";
                    smtpPort = 465;
                    socketOptions = SecureSocketOptions.SslOnConnect;
                }
                else
                {
                    // Email Aruba ordinaria
                    smtpHost = "smtps.aruba.it";
                    smtpPort = 465;
                    socketOptions = SecureSocketOptions.SslOnConnect;
                }
            }

            else
                throw new InvalidOperationException($"Provider {casella.Provider} non gestito");

            using var client = new SmtpClient { Timeout = 10000 };
            await client.ConnectAsync(smtpHost, smtpPort, socketOptions, ct);
            await client.AuthenticateAsync(casella.Email, casella.Password, ct);
            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);

            _logger.LogInformation("✉️ Email inviata da {From} ({Display}) a {To}", casella.Email, nomeMittente, to);
        }



        private static string CleanMime(string? raw)
        {
            raw = (raw ?? "").Trim();

            if (string.IsNullOrWhiteSpace(raw))
                return "application/octet-stream";

            // normalizzazioni comuni
            if (string.Equals(raw, "image/jpg", StringComparison.OrdinalIgnoreCase))
                raw = "image/jpeg";

            // se manca lo slash non è un mime-type
            if (!raw.Contains('/'))
                return "application/octet-stream";

            return raw;
        }

        private static ContentType ToContentTypeSafe(string? raw, ILogger logger, string fileName)
        {
            var cleaned = CleanMime(raw);

            if (ContentType.TryParse(cleaned, out var ct))
                return ct;

            logger.LogWarning("⚠️ MimeType non valido per allegato {FileName}: '{Raw}' -> fallback octet-stream",
                fileName, raw);

            return new ContentType("application", "octet-stream");
        }

        public class EmailTask
        {
            public int Id { get; set; }
            public int EmailId { get; set; }
            public string Utente { get; set; } = default!;
            public string? Commento { get; set; }
            public DateTime DataCreazione { get; set; } = DateTime.Now;
            public string Titolo { get; set; } = string.Empty;
            public string Stato { get; set; } = "APERTO";
            public DateTime? DataChiusura { get; set; }
        }

        public class EmailTaskComment
        {
            public int Id { get; set; }
            public int TaskId { get; set; }
            public string Utente { get; set; } = "";
            public string Testo { get; set; } = "";
            public DateTime DataCreazione { get; set; }
            public int? ReplyTo { get; set; }

            // NEW
            public string IsDone { get; set; } = "N"; // 'Y' / 'N'
        }
        public async Task<UserTaskItem_NEW> CreateTaskForEmailAsync(
    int emailId,
    string utente,
    string? commento,
    CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();

            // Recupera la mail
            var email = await db.EmailRicevute
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == emailId, ct);

            if (email == null)
                throw new InvalidOperationException($"Email {emailId} non trovata");

            // Crea il record del task
            var task = new EmailTask
            {
                EmailId = emailId,
                Utente = utente,
                Commento = commento,
                Titolo = email.Oggetto ?? "(senza oggetto)",
                Stato = "APERTO",
                DataCreazione = DateTime.Now
            };

            db.EmailTasks.Add(task);
            await db.SaveChangesAsync(ct);

            // ✅ restituisce UserTaskItem_NEW
            return new SCemail.Components.Data.UserTaskItem_NEW(
                 Id: task.Id,
                 Utente: task.Utente,
                 EmailId: task.EmailId.ToString(),
                 Titolo: task.Titolo,
                 Commento: task.Commento,
                 Stato: task.Stato,
                 DataCreazione: task.DataCreazione
             );

        }

        public async Task AddRecipientIfNotExistsAsync(string email, string? nome = null, CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();

            if (string.IsNullOrWhiteSpace(email))
                return;

            // normalizza email
            email = email.Trim().ToLowerInvariant();

            // controllo se già esiste
           var existing = await db.EmailDestinatari
            .FirstOrDefaultAsync(e => e.Email.ToLower() == email, ct);

            if (existing != null)
                return;


            int nextId = (await db.EmailDestinatari
           .Select(e => (int?)e.Id)
           .MaxAsync(ct)) ?? 0;
            nextId++;

            // aggiungi il nuovo destinatario
            db.EmailDestinatari.Add(new EmailDestinatario
            {
                Id = nextId,
                Email = email
            });

            await db.SaveChangesAsync(ct);
            _logger.LogInformation("📬 Aggiunto nuovo destinatario: {Email}", email);
        }


        // 🔹 Connessione diretta Oracle (usata per query manuali tipo GetConversationByThreadAsync)
        private async Task<OracleConnection> GetOpenConnectionAsync()
        {
            // Recupera la connection string dal tuo DbContextFactory
            await using var db = _dbFactory.CreateDbContext();
            var conn = new OracleConnection(db.Database.GetConnectionString());
            await conn.OpenAsync();
            return conn;
        }

        public async Task MarkDraftAsReadAsync(long draftId, string utente)
        {
            using var db = _dbFactory.CreateDbContext();

            var bozza = await db.EmailBozze
                .FirstOrDefaultAsync(x => x.Id == draftId && x.Utente == utente);

            if (bozza != null && !bozza.Letto)
            {
                bozza.Letto = true;
                await db.SaveChangesAsync();
            }
        }

        public async Task<int> CountUnreadDraftsAsync(string utente)
        {
            using var db = _dbFactory.CreateDbContext();

            return await db.EmailBozze
                .Where(x => x.Utente == utente && !x.Letto)
                .CountAsync();
        }
        public async Task<List<EmailInboxSezione>> GetSezioniInboxAsync(bool soloAttive = true)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var q = db.EmailInboxSezioni.AsNoTracking();

            if (soloAttive)
                q = q.Where(s => s.Attiva == "Y");

            return await q.OrderBy(s => s.Ordine).ThenBy(s => s.Nome).ToListAsync();
        }

        public async Task<long?> GetSezioneCorrenteAsync(long emailId, string utente)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var map = await db.EmailInboxSezioneMap.AsNoTracking()
                .FirstOrDefaultAsync(x => x.IdEmail == emailId && x.Utente == utente);

            return map?.IdSezione;
        }

        public async Task SpostaEmailInSezioneAsync(long emailId, long idSezione, string utente, string? updatedBy = null)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var existing = await db.EmailInboxSezioneMap
                .FirstOrDefaultAsync(x => x.IdEmail == emailId && x.Utente == utente);

            if (existing == null)
            {
                db.EmailInboxSezioneMap.Add(new EmailInboxSezioneMap
                {
                    IdEmail = emailId,
                    IdSezione = idSezione,
                    Utente = utente,
                    UpdatedAt = DateTime.Now,
                    UpdatedBy = updatedBy
                });
            }
            else
            {
                existing.IdSezione = idSezione;
                existing.UpdatedAt = DateTime.Now;
                existing.UpdatedBy = updatedBy;
            }

            await db.SaveChangesAsync();
        }

        public async Task<List<string>> GetRecentRecipientsAsync(int max = 10, CancellationToken ct = default)
        {
            var list = new List<string>();
            await using var conn = new OracleConnection(_connectionString);
            await conn.OpenAsync(ct);

            const string sql = @"
            SELECT email
              FROM (
                    SELECT DISTINCT email
                      FROM (
                            SELECT LOWER(TRIM(destinatario)) AS email
                              FROM sgapp.email_destinatari
                             WHERE destinatario LIKE '%@%'

                            UNION

                            SELECT LOWER(TRIM(email)) AS email
                              FROM sgapp.rubrica_contatti
                             WHERE email LIKE '%@%'
                           )
                   )
             WHERE ROWNUM <= :max";

            await using var cmd = new OracleCommand(sql, conn);
            cmd.BindByName = true;
            cmd.Parameters.Add("max", OracleDbType.Int32).Value = max;

            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                var email = rdr.IsDBNull(0) ? null : rdr.GetString(0)?.Trim();
                if (!string.IsNullOrWhiteSpace(email))
                    list.Add(email);
            }

            return list;
        }


        public async Task SegnaLettaAsync(int emailId, string utente)
        {
            await using var db = _dbFactory.CreateDbContext();

            // Se già segnato come letto → non fare nulla
            var exists = await db.EmailLetture
                     .Where(x => x.EmailId == emailId && x.Utente == utente)
                     .CountAsync() > 0;

            if (!exists)
            {
                db.EmailLetture.Add(new EmailLetturaUtente
                {
                    EmailId = emailId,
                    Utente = utente,
                    DataLettura = DateTime.Now
                });

                await db.SaveChangesAsync();
            }
        }

        public async Task<int> CountUnreadAsync(string utente)
        {
            await using var db = _dbFactory.CreateDbContext();

            // 1️⃣ Prendo tutte le email assegnate all’utente (escludendo SOLO_INVIO)
            var assegnate = db.EmailAssegnazione
                .Where(a => a.Utente == utente && (a.SoloInvio ?? "N") == "N");

            // 2️⃣ Prendo tutte le email già lette dall’utente
            var lette = db.EmailLetture
                .Where(l => l.Utente == utente)
                .Select(l => l.EmailId);

            // 3️⃣ Le non lette sono: assegnate EXCEPT lette
            var count = await assegnate
                .Where(a => !lette.Contains(a.EmailId))
                .CountAsync();

            return count;
        }

        public async Task CreateNotificationAsync(
    int emailId,
    string utente,
    string tipoEvento,
    int? commentoId = null)
        {
            using var db = _dbFactory.CreateDbContext();

            // evita duplicati NON visti
            bool exists = await db.emailMenzionis.AnyAsync(m =>
                m.EmailId == emailId &&
                m.Utente == utente &&
                m.Visto == "N" &&
                m.TipoEvento == tipoEvento);

            if (exists) return;

            db.emailMenzionis.Add(new EmailMenzione
            {
                EmailId = emailId,
                Utente = utente,
                DataMenzione = DateTime.Now,
                CommentoId = commentoId,
                Visto = "N",
                TipoEvento = tipoEvento
            });

            await db.SaveChangesAsync();
        }

        public async Task SegnaNotificheVisteAsync(int emailId, string utente)
        {
            using var db = await _dbFactory.CreateDbContextAsync();

            var notifiche = await db.emailMenzionis
                .Where(m =>
                    m.EmailId == emailId &&
                    m.Utente == utente &&
                    m.Visto == "N")
                .ToListAsync();

            if (!notifiche.Any())
                return;

            foreach (var n in notifiche)
            {
                n.Visto = "Y";
            }

            await db.SaveChangesAsync();
        }
        public async Task<(List<EmailListItem_NEW> Page, int Total)> SearchEmailsAdvancedAsync(
    List<int> casellaIds,
    int start,
    int pageSize,
    string? word,
    string? address,
    string? folderUi = null,
    string? utente = null)
        {
            using var conn = new OracleConnection(_connectionString);
            await conn.OpenAsync();

            var w = (word ?? "").Trim();
            var a = (address ?? "").Trim();

            bool hasWord = !string.IsNullOrWhiteSpace(w) && w.Length >= 2;
            bool hasAddr = !string.IsNullOrWhiteSpace(a) && a.Length >= 2;

            if (!hasWord && !hasAddr)
                return (new List<EmailListItem_NEW>(), 0);

            if (casellaIds == null || casellaIds.Count == 0)
                return (new List<EmailListItem_NEW>(), 0);

            var inList = string.Join(",", casellaIds);

            var sql = $@"
SELECT
    e.ID,
    e.MITTENTE,
    e.DESTINATARI,
    e.OGGETTO,
    e.DATA_RICEZIONE,
    e.CASELLA_ID,
    c.EMAIL AS CASELLA_EMAIL,
    e.APERTO,
    e.CC,
    e.CCN,
    CASE WHEN EXISTS (
    SELECT 1 FROM SGAPP.EMAIL_ALLEGATI a WHERE a.EMAIL_ID = e.ID
) THEN 1 ELSE 0 END AS HAS_ATTACH,
(
    SELECT LISTAGG(
        a.ID || '::' || a.NOME_FILE || '::' || NVL(a.MIME_TYPE,''),
        '||'
    ) WITHIN GROUP (ORDER BY a.ID)
    FROM SGAPP.EMAIL_ALLEGATI a
    WHERE a.EMAIL_ID = e.ID
) AS ATT_PACK,
SUBSTR(NVL(e.CORPO_TESTO, e.CORPO_HTML), 1, 200) AS PREVIEW,
    SUBSTR(NVL(e.CORPO_TESTO, e.CORPO_HTML), 1, 200) AS PREVIEW,
    (
        SELECT LISTAGG(x.UTENTE, ';') WITHIN GROUP (ORDER BY x.ID)
        FROM SGAPP.EMAIL_ASSEGNAZIONI x
        WHERE x.EMAIL_ID = e.ID
    ) AS ASSEGNATO_A,
    COUNT(*) OVER() AS TOTAL_COUNT
FROM SGAPP.EMAIL_RICEVUTE e
JOIN SGAPP.CASELLEPOSTA c ON c.ID = e.CASELLA_ID
WHERE NVL(e.ELIMINATO,'N') = 'N'
  AND e.CASELLA_ID IN ({inList})
";

            // ---- filtro cartella logica ----
            if (!string.IsNullOrWhiteSpace(folderUi))
            {
                var f = folderUi.Trim().ToLowerInvariant();

                if (f == "inbox")
                {
                    if (string.IsNullOrWhiteSpace(utente))
                        throw new ArgumentException("utente obbligatorio quando folderUi = inbox", nameof(utente));

                    sql += @"
  AND EXISTS (
        SELECT 1
        FROM SGAPP.EMAIL_ASSEGNAZIONI x
        WHERE x.EMAIL_ID = e.ID
          AND x.UTENTE = :p_utente
  )
  AND NOT EXISTS (
        SELECT 1
        FROM SGAPP.EMAIL_ARCHIVIO ar
        WHERE ar.ID_EMAIL = e.ID
          AND ar.UTENTE = :p_utente
  )
  AND NOT EXISTS (
        SELECT 1
        FROM SGAPP.EMAIL_INBOX_SEZIONE_MAP m
        WHERE m.ID_EMAIL = e.ID
          AND m.UTENTE = :p_utente
  )";
                }
                else if (f == "myarchive")
                {
                    if (string.IsNullOrWhiteSpace(utente))
                        throw new ArgumentException("utente obbligatorio quando folderUi = myarchive", nameof(utente));

                    sql += @"
  AND EXISTS (
        SELECT 1
        FROM SGAPP.EMAIL_ARCHIVIO ar
        WHERE ar.ID_EMAIL = e.ID
          AND ar.UTENTE = :p_utente
  )";
                }
            }

            // ✅ filtro word - LIKE su header + nome allegato
            if (hasWord)
            {
                sql += @"
  AND (
        LOWER(NVL(e.OGGETTO,''))      LIKE '%' || :p_word || '%'
     OR LOWER(NVL(e.MITTENTE,''))     LIKE '%' || :p_word || '%'
     OR LOWER(NVL(e.DESTINATARI,''))  LIKE '%' || :p_word || '%'
     OR LOWER(NVL(e.CC,''))           LIKE '%' || :p_word || '%'
     OR LOWER(NVL(e.CCN,''))          LIKE '%' || :p_word || '%'
     OR EXISTS (
           SELECT 1
           FROM SGAPP.EMAIL_ALLEGATI a
           WHERE a.EMAIL_ID = e.ID
             AND LOWER(NVL(a.NOME_FILE,'')) LIKE '%' || :p_word || '%'
     )
  )";
            }

            // ✅ filtro address - LIKE su header (con NVL per coerenza)
            if (hasAddr)
            {
                sql += @"
  AND (
        LOWER(NVL(e.MITTENTE,''))     LIKE '%' || :p_addr || '%'
     OR LOWER(NVL(e.DESTINATARI,'')) LIKE '%' || :p_addr || '%'
     OR LOWER(NVL(e.CC,''))          LIKE '%' || :p_addr || '%'
     OR LOWER(NVL(e.CCN,''))         LIKE '%' || :p_addr || '%'
  )";
            }

            sql += @"
ORDER BY e.DATA_RICEZIONE DESC
OFFSET :p_offset ROWS FETCH NEXT :p_limit ROWS ONLY";

            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };

            if (!string.IsNullOrWhiteSpace(folderUi) &&
                (folderUi.Equals("inbox", StringComparison.OrdinalIgnoreCase) ||
                 folderUi.Equals("myarchive", StringComparison.OrdinalIgnoreCase)))
            {
                cmd.Parameters.Add("p_utente", OracleDbType.Varchar2).Value = utente!;
            }

            // Parametri normalizzati
            string? pWord = null;
            string? pAddr = null;

            if (hasWord)
            {
                pWord = w.Trim().ToLowerInvariant().Replace("\\", ""); // safe
                cmd.Parameters.Add("p_word", OracleDbType.Varchar2).Value = pWord;
            }

            if (hasAddr)
            {
                pAddr = a.Trim().ToLowerInvariant().Replace("\\", "");
                cmd.Parameters.Add("p_addr", OracleDbType.Varchar2).Value = pAddr;
            }

            cmd.Parameters.Add("p_offset", OracleDbType.Int32).Value = start;
            cmd.Parameters.Add("p_limit", OracleDbType.Int32).Value = pageSize;

            // 🔥 LOG COMPLETO: metodo + contesto + SQL + parametri
            try
            {
                _logger.LogWarning(
                    "### SearchEmailsAdvancedAsync ### folderUi={Folder} utente={Utente} caselle=[{Caselle}] start={Start} limit={Limit} wordRaw='{WordRaw}' addrRaw='{AddrRaw}' p_word='{PWord}' p_addr='{PAddr}'",
                    folderUi, utente, inList, start, pageSize, w, a, pWord, pAddr);

                _logger.LogWarning("SQL:\n{Sql}", sql);

                foreach (OracleParameter p in cmd.Parameters)
                    _logger.LogWarning("PARAM {Name}={Value}", p.ParameterName, p.Value);
            }
            catch { /* no-op */ }

            // ✅ Optional: check mirato su email 73059 per capire al volo se matcha allegati
            if (hasWord)
            {
                await using var chk = new OracleCommand(@"
SELECT COUNT(*)
FROM sgapp.email_allegati
WHERE email_id = 73059
  AND LOWER(NVL(nome_file,'')) LIKE '%' || :pw || '%'", conn);
                chk.Parameters.Add("pw", OracleDbType.Varchar2).Value = pWord!;
                var cnt = Convert.ToInt32(await chk.ExecuteScalarAsync());
                _logger.LogWarning("CHK allegato email_id=73059 matchCount={Cnt} (pw='{Pw}')", cnt, pWord);
            }

            var list = new List<EmailListItem_NEW>();
            int total = 0;

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (total == 0)
                    total = reader.GetInt32(reader.GetOrdinal("TOTAL_COUNT"));
                string? attPack = GetStr(reader, "ATT_PACK");

                List<AllegatoItem_NEW>? allegati = null;
                if (!string.IsNullOrWhiteSpace(attPack))
                {
                    allegati = attPack
                        .Split("||", StringSplitOptions.RemoveEmptyEntries)
                        .Select(x =>
                        {
                            var parts = x.Split("::");
                            var id = int.Parse(parts[0]);
                            var nome = parts.Length > 1 ? parts[1] : "";
                            var mime = parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]) ? parts[2] : null;
                            return new AllegatoItem_NEW(id, nome, mime);
                        })
                        .ToList();
                }
                list.Add(new EmailListItem_NEW(
                    Id: reader.GetInt32(reader.GetOrdinal("ID")),
                    Data: reader.IsDBNull(reader.GetOrdinal("DATA_RICEZIONE")) ? (DateTime?)null : reader.GetDateTime(reader.GetOrdinal("DATA_RICEZIONE")),
                    Mittente: GetStr(reader, "MITTENTE") ?? "",
                    Oggetto: GetStr(reader, "OGGETTO") ?? "(senza oggetto)",
                    Aperto: GetStr(reader, "APERTO") ?? "",
                    HasAttachments: reader.GetInt32(reader.GetOrdinal("HAS_ATTACH")) == 1,
                    ThreadLen: 1,
                    Replies: 0,
                    Preview: GetStr(reader, "PREVIEW"),
                    Allegati: allegati,
                    MessageId: null,
                    CasellaId: reader.GetInt32(reader.GetOrdinal("CASELLA_ID")),
                    CasellaEmail: GetStr(reader, "CASELLA_EMAIL"),
                    AssegnatoA: GetStr(reader, "ASSEGNATO_A"),
                    Destinatari: GetStr(reader, "DESTINATARI"),
                    Cc: GetStr(reader, "CC"),
                    Ccn: GetStr(reader, "CCN")
                ));
            }

            return (list, total);
        }


        /// <summary>
        /// Trasforma input utente in query Oracle Text "prefix" sicura.
        /// Esempi:
        ///  "ra"       -> "ra%"
        ///  "mario ro" -> "mario% AND ro%"
        /// Protegge operatori Oracle Text (AND/OR/NOT/WITHIN...) con { }.
        /// Usa wildcard % (non *).
        /// </summary>
        private static string ToOracleTextPrefixQuery(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";

            // token alfanumerici (niente caratteri speciali che spaccano il parser)
            var rawTokens = Regex.Matches(input.Trim(), @"[\p{L}\p{N}]+")
                                 .Select(m => m.Value)
                                 .Where(t => t.Length >= 2)
                                 .Select(t => t.ToLowerInvariant())
                                 .ToList();

            if (rawTokens.Count == 0) return "";

            // parole riservate/operatori comuni in Oracle Text
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "and","or","not","near","within","about","accum","minus","fuzzy","soundex"
    };

            // ✅ wildcard Oracle Text: % e _
            // prefix search: token%
            var tokens = rawTokens.Select(t =>
            {
                var safe = reserved.Contains(t) ? "{" + t + "}" : t;
                return safe + "%";
            });

            return string.Join(" AND ", tokens);
        }

        public async Task<(List<EmailListItem_NEW>, int)> GetEmailPageInSezioneAsync(
     long idSezione,
     string utente,
     List<int> casellaIds,
     int start,
     int pageSize,
     string? word = null,
     string? address = null)
        {
            using var conn = new OracleConnection(_connectionString);
            await conn.OpenAsync();

            var w = (word ?? "").Trim();
            var a = (address ?? "").Trim();

            bool hasWord = !string.IsNullOrWhiteSpace(w) && w.Length >= 2;
            bool hasAddr = !string.IsNullOrWhiteSpace(a) && a.Length >= 2;

            // ⚠️ qui manteniamo la tua IN(...) come avevi già.
            // Se vuoi, dopo ti do anche la variante "safe" con TABLE(:ids).
            var inList = string.Join(",", casellaIds);

            var sql = $@"
SELECT
    e.ID,
    e.MITTENTE,
    e.DESTINATARI,
    e.OGGETTO,
    e.DATA_RICEZIONE,
    e.CASELLA_ID,
    c.EMAIL as CASELLA_EMAIL,
    e.APERTO,
    e.CC,
    e.CCN,

    CASE WHEN EXISTS (
        SELECT 1 FROM SGAPP.EMAIL_ALLEGATI al WHERE al.EMAIL_ID = e.ID
    ) THEN 1 ELSE 0 END AS HAS_ATTACH,

    (
        SELECT LISTAGG(
            al.ID || '::' || al.NOME_FILE || '::' || NVL(al.MIME_TYPE,''),
            '||'
        ) WITHIN GROUP (ORDER BY al.ID)
        FROM SGAPP.EMAIL_ALLEGATI al
        WHERE al.EMAIL_ID = e.ID
    ) AS ATT_PACK,

    SUBSTR(NVL(e.CORPO_TESTO, e.CORPO_HTML), 1, 200) AS PREVIEW,

    (
        SELECT LISTAGG(x.UTENTE, ';') WITHIN GROUP (ORDER BY x.ID)
        FROM SGAPP.EMAIL_ASSEGNAZIONI x
        WHERE x.EMAIL_ID = e.ID
    ) AS ASSEGNATO_A

FROM SGAPP.EMAIL_RICEVUTE e
JOIN SGAPP.CASELLEPOSTA c ON c.ID = e.CASELLA_ID

WHERE e.CASELLA_ID IN ({inList})

  AND EXISTS (
      SELECT 1
      FROM SGAPP.EMAIL_ASSEGNAZIONI x
      WHERE x.EMAIL_ID = e.ID
        AND x.UTENTE = :utente
  )

  AND NOT EXISTS (
      SELECT 1
      FROM SGAPP.EMAIL_ARCHIVIO ar
      WHERE ar.ID_EMAIL = e.ID
        AND ar.UTENTE = :utente
  )

  AND EXISTS (
      SELECT 1
      FROM SGAPP.EMAIL_INBOX_SEZIONE_MAP m
      WHERE m.ID_EMAIL = e.ID
        AND m.UTENTE = :utente
        AND m.ID_SEZIONE = :idSezione
  )
";

            // ✅ filtro "parola" identico alla inbox (OGGETTO/MITTENTE/DEST/TESTO/HTML)
            if (hasWord)
            {
                sql += @"
  AND (
        LOWER(NVL(e.OGGETTO,''))       LIKE :word
     OR LOWER(NVL(e.MITTENTE,''))      LIKE :word
     OR LOWER(NVL(e.DESTINATARI,''))   LIKE :word
     OR LOWER(NVL(e.CORPO_TESTO,''))   LIKE :word
     OR LOWER(NVL(e.CORPO_HTML,''))    LIKE :word
  )
";
            }

            // ✅ filtro "indirizzo" (rubrica): mittente/destinatari/cc/ccn
            if (hasAddr)
            {
                sql += @"
  AND (
        LOWER(NVL(e.MITTENTE,''))      LIKE :addr
     OR LOWER(NVL(e.DESTINATARI,''))   LIKE :addr
     OR LOWER(NVL(e.CC,''))            LIKE :addr
     OR LOWER(NVL(e.CCN,''))           LIKE :addr
  )
";
            }

            sql += @"
ORDER BY e.DATA_RICEZIONE DESC
OFFSET :p_start ROWS FETCH NEXT :p_pageSize ROWS ONLY";

            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
            cmd.Parameters.Add("utente", OracleDbType.Varchar2).Value = utente;
            cmd.Parameters.Add("idSezione", OracleDbType.Int64).Value = idSezione;

            if (hasWord)
                cmd.Parameters.Add("word", OracleDbType.Varchar2).Value = $"%{w.ToLower()}%";

            if (hasAddr)
                cmd.Parameters.Add("addr", OracleDbType.Varchar2).Value = $"%{a.ToLower()}%";

            cmd.Parameters.Add("p_start", OracleDbType.Int32).Value = start;
            cmd.Parameters.Add("p_pageSize", OracleDbType.Int32).Value = pageSize;

            var list = new List<EmailListItem_NEW>();

            await using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    string? attPack = reader.IsDBNull("ATT_PACK") ? null : reader.GetString("ATT_PACK");

                    List<AllegatoItem_NEW>? allegati = null;
                    if (!string.IsNullOrWhiteSpace(attPack))
                    {
                        allegati = attPack
                            .Split("||", StringSplitOptions.RemoveEmptyEntries)
                            .Select(x =>
                            {
                                var parts = x.Split("::");
                                var id = int.Parse(parts[0]);
                                var nome = parts.Length > 1 ? parts[1] : "";
                                var mime = parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]) ? parts[2] : null;
                                return new AllegatoItem_NEW(id, nome, mime);
                            })
                            .ToList();
                    }

                    list.Add(new EmailListItem_NEW(
                        Id: reader.GetInt32("ID"),
                        Data: reader.IsDBNull("DATA_RICEZIONE") ? (DateTime?)null : reader.GetDateTime("DATA_RICEZIONE"),
                        Mittente: reader.IsDBNull("MITTENTE") ? "" : reader.GetString("MITTENTE"),
                        Oggetto: reader.IsDBNull("OGGETTO") ? "" : reader.GetString("OGGETTO"),
                        Aperto: reader.IsDBNull("APERTO") ? "" : reader.GetString("APERTO"),
                        HasAttachments: reader.GetInt32("HAS_ATTACH") == 1,
                        ThreadLen: 1,
                        Replies: 0,
                        Preview: reader.IsDBNull("PREVIEW") ? null : reader.GetString("PREVIEW"),
                        Allegati: allegati,
                        MessageId: null,
                        CasellaId: reader.GetInt32("CASELLA_ID"),
                        CasellaEmail: reader.IsDBNull("CASELLA_EMAIL") ? null : reader.GetString("CASELLA_EMAIL"),
                        AssegnatoA: reader.IsDBNull("ASSEGNATO_A") ? null : reader.GetString("ASSEGNATO_A"),
                        Destinatari: GetStr(reader, "DESTINATARI"),
                        Cc: GetStr(reader, "CC"),
                        Ccn: GetStr(reader, "CCN")
                    ));
                }
            }

            // =========================
            // COUNT coerente col SELECT
            // =========================
            var countSql = $@"
SELECT COUNT(*)
FROM SGAPP.EMAIL_RICEVUTE e
WHERE e.CASELLA_ID IN ({inList})

  AND EXISTS (
      SELECT 1
      FROM SGAPP.EMAIL_ASSEGNAZIONI x
      WHERE x.EMAIL_ID = e.ID
        AND x.UTENTE = :utente
  )

  AND NOT EXISTS (
      SELECT 1
      FROM SGAPP.EMAIL_ARCHIVIO ar
      WHERE ar.ID_EMAIL = e.ID
        AND ar.UTENTE = :utente
  )

  AND EXISTS (
      SELECT 1
      FROM SGAPP.EMAIL_INBOX_SEZIONE_MAP m
      WHERE m.ID_EMAIL = e.ID
        AND m.UTENTE = :utente
        AND m.ID_SEZIONE = :idSezione
  )
";

            if (hasWord)
            {
                countSql += @"
  AND (
        LOWER(NVL(e.OGGETTO,''))       LIKE :word
     OR LOWER(NVL(e.MITTENTE,''))      LIKE :word
     OR LOWER(NVL(e.DESTINATARI,''))   LIKE :word
     OR LOWER(NVL(e.CORPO_TESTO,''))   LIKE :word
     OR LOWER(NVL(e.CORPO_HTML,''))    LIKE :word
  )
";
            }

            if (hasAddr)
            {
                countSql += @"
  AND (
        LOWER(NVL(e.MITTENTE,''))      LIKE :addr
     OR LOWER(NVL(e.DESTINATARI,''))   LIKE :addr
     OR LOWER(NVL(e.CC,''))            LIKE :addr
     OR LOWER(NVL(e.CCN,''))           LIKE :addr
  )
";
            }

            await using var countCmd = new OracleCommand(countSql, conn) { BindByName = true };
            countCmd.Parameters.Add("utente", OracleDbType.Varchar2).Value = utente;
            countCmd.Parameters.Add("idSezione", OracleDbType.Int64).Value = idSezione;

            if (hasWord)
                countCmd.Parameters.Add("word", OracleDbType.Varchar2).Value = $"%{w.ToLower()}%";

            if (hasAddr)
                countCmd.Parameters.Add("addr", OracleDbType.Varchar2).Value = $"%{a.ToLower()}%";

            var total = Convert.ToInt32(await countCmd.ExecuteScalarAsync());

            return (list, total);
        }

        private async Task AddAssignActivityAsync(
      OracleConnection conn,
      OracleTransaction tx,
      int emailId,
      string eseguitoDa,
      string assegnatoA,
      CancellationToken ct = default)
        {
            var testo = $"[[ASSIGN]] {eseguitoDa} ha assegnato l'email a {assegnatoA}";

            const string sql = @"
INSERT INTO SGAPP.COMMENTI_EMAIL (EMAIL_ID, AUTORE, TESTO, DATA_CREAZIONE)
VALUES (:p_eid, :p_autore, :p_testo, SYSDATE)";

            await using var cmd = new OracleCommand(sql, conn)
            {
                BindByName = true,
                Transaction = tx
            };

            cmd.Parameters.Add("p_eid", OracleDbType.Int32).Value = emailId;
            cmd.Parameters.Add("p_autore", OracleDbType.Varchar2, 200).Value = "SYSTEM";
            cmd.Parameters.Add("p_testo", OracleDbType.Clob).Value = testo;

            await cmd.ExecuteNonQueryAsync(ct);
        }


        public record SentAttachmentMiniDto(int Id, string NomeFile, string MimeType);

        public record SentEmailListItemDto(
            int Id,
            DateTime DataInvio,
            string? CasellaMittente,
            string? Destinatari,
            string? Oggetto,
            string Preview,
            List<SentAttachmentMiniDto> Allegati
        );

        public record SentEmailDetailDto(
            int Id,
            DateTime DataInvio,
            string? CasellaMittente,
            string? Destinatari,
            string? Cc,
            string? Ccn,
            string? Oggetto,
            string? CorpoHtml,
            string? CorpoTesto,
            string? MessageId,
            List<SentAttachmentMiniDto> Allegati
        );

        public record PagedResult<T>(List<T> Page, int Total);

        public async Task<PagedResult<SentEmailListItemDto>> GetSentPagedAsync(
            string utente,
            int start,
            int pageSize,
            string? searchText = null,
            CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();

            var q = db.EmailInviate
                .AsNoTracking()
                .Where(x => x.Utente == utente);

            // ricerca semplice su oggetto/destinatari (sent)
            var s = (searchText ?? "").Trim();
            if (s.Length >= 2)
            {
                q = q.Where(x =>
                    (x.Oggetto ?? "").ToLower().Contains(s.ToLower()) ||
                    (x.Destinatari ?? "").ToLower().Contains(s.ToLower()) ||
                    (x.Cc ?? "").ToLower().Contains(s.ToLower()) ||
                    (x.Bcc ?? "").ToLower().Contains(s.ToLower()));
            }

            var total = await q.CountAsync(ct);

            // pagina
            var page = await q
                .OrderByDescending(x => x.DataInvio)
                .Skip(start)
                .Take(pageSize)
                .Select(x => new
                {
                    x.Id,
                    x.DataInvio,
                    CasellaMittente = x.Casella.Email,
                    x.CasellaId,
                    x.Destinatari,
                    x.Oggetto,
                    Preview = (x.CorpoTesto ?? x.CorpoHtml ?? "")
                })
                .ToListAsync(ct);

            // allegati: query unica per tutti gli id pagina (evita N+1)
            var ids = page.Select(p => p.Id).ToList();

            var att = await db.InviataAllegati
                .AsNoTracking()
                .Where(a => ids.Contains(a.EmailId))
                .Select(a => new
                {
                    a.EmailId,
                    a.Id,
                    a.NomeFile,
                    a.MimeType
                })
                .ToListAsync(ct);

            var attMap = att
                .GroupBy(a => a.EmailId)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(x => new SentAttachmentMiniDto(x.Id, x.NomeFile, x.MimeType)).ToList()
                );

            string Strip(string htmlOrText)
            {
                if (string.IsNullOrWhiteSpace(htmlOrText)) return "";
                // il tuo StripHtml va bene, ma qui lo tengo locale per completezza:
                var plain = System.Text.RegularExpressions.Regex.Replace(htmlOrText, "<.*?>", " ");
                plain = System.Net.WebUtility.HtmlDecode(plain);
                plain = System.Text.RegularExpressions.Regex.Replace(plain, @"\s+", " ").Trim();
                return plain;
            }

            var dto = page.Select(p => new SentEmailListItemDto(
                Id: p.Id,
                DataInvio: p.DataInvio,
                CasellaMittente: p.CasellaMittente,
                Destinatari: p.Destinatari,
                Oggetto: p.Oggetto,
                Preview: Strip(p.Preview),
                Allegati: attMap.TryGetValue(p.Id, out var list) ? list : new()
            )).ToList();

            return new(dto, total);
        }

        public async Task<SentEmailDetailDto?> GetSentDetailAsync(int id, string utente, CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();

            var mail = await db.EmailInviate
                .AsNoTracking()
                .Where(x => x.Id == id && x.Utente == utente)
                .Select(x => new
                {
                    x.Id,
                    x.DataInvio,
                    CasellaMittente = x.Casella.Email,
                    x.CasellaId,
                    x.Destinatari,
                    x.Cc,
                    x.Bcc,
                    x.Oggetto,
                    x.CorpoHtml,
                    x.CorpoTesto,
                    x.MessageId
                })
                .FirstOrDefaultAsync(ct);

            if (mail == null) return null;

            var allegati = await db.InviataAllegati
                .AsNoTracking()
                .Where(a => a.EmailId == id)
                .OrderBy(a => a.NomeFile)
                .Select(a => new SentAttachmentMiniDto(a.Id, a.NomeFile, a.MimeType))
                .ToListAsync(ct);

            return new SentEmailDetailDto(
                Id: mail.Id,
                DataInvio: mail.DataInvio,
                CasellaMittente: mail.CasellaMittente,
                Destinatari: mail.Destinatari,
                Cc: mail.Cc,
                Ccn: mail.Bcc,
                Oggetto: mail.Oggetto,
                CorpoHtml: mail.CorpoHtml,
                CorpoTesto: mail.CorpoTesto,
                MessageId: mail.MessageId,
                Allegati: allegati
            );
        }

        public async Task<int> CountUnassignedForAdminAsync(string utente, string? word = null, string? address = null)
        {
            var caselleAdmin = await GetAdminCasellaIdsAsync(utente);
            if (caselleAdmin.Count == 0) return 0;

            using var conn = new OracleConnection(_connectionString);
            await conn.OpenAsync();

            var w = (word ?? "").Trim().ToLowerInvariant();
            var a = (address ?? "").Trim().ToLowerInvariant();
            bool hasWord = w.Length >= 2;
            bool hasAddr = a.Length >= 2;

            var inList = string.Join(",", caselleAdmin);

            var sql = $@"
SELECT COUNT(*)
FROM SGAPP.EMAIL_RICEVUTE e
WHERE NVL(e.ELIMINATO,'N') = 'N'
  AND e.CASELLA_ID IN ({inList})
  AND NOT EXISTS (
        SELECT 1
        FROM SGAPP.EMAIL_ASSEGNAZIONI x
        WHERE x.EMAIL_ID = e.ID
  )
";

            if (hasWord)
            {
                sql += @"
  AND (
        LOWER(NVL(e.OGGETTO,''))      LIKE '%' || :p_word || '%'
     OR LOWER(NVL(e.MITTENTE,''))     LIKE '%' || :p_word || '%'
     OR LOWER(NVL(e.DESTINATARI,''))  LIKE '%' || :p_word || '%'
     OR LOWER(NVL(e.CC,''))           LIKE '%' || :p_word || '%'
     OR LOWER(NVL(e.CCN,''))          LIKE '%' || :p_word || '%'
     OR EXISTS (
           SELECT 1
           FROM SGAPP.EMAIL_ALLEGATI al
           WHERE al.EMAIL_ID = e.ID
             AND LOWER(NVL(al.NOME_FILE,'')) LIKE '%' || :p_word || '%'
     )
  )
";
            }

            if (hasAddr)
            {
                sql += @"
  AND (
        LOWER(NVL(e.MITTENTE,''))     LIKE '%' || :p_addr || '%'
     OR LOWER(NVL(e.DESTINATARI,'')) LIKE '%' || :p_addr || '%'
     OR LOWER(NVL(e.CC,''))          LIKE '%' || :p_addr || '%'
     OR LOWER(NVL(e.CCN,''))         LIKE '%' || :p_addr || '%'
  )
";
            }

            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };

            if (hasWord) cmd.Parameters.Add("p_word", OracleDbType.Varchar2).Value = w;
            if (hasAddr) cmd.Parameters.Add("p_addr", OracleDbType.Varchar2).Value = a;

            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }


        public async Task<(List<EmailListItem_NEW> Page, int Total)> GetUnassignedForAdminPagedAsync(
     string utente,
     int start,
     int pageSize,
     string? word,
     string? address // lo tengo per firma ma NON lo uso (mi hai chiesto solo oggetto+mittente)
 )
        {
            var caselleAdmin = await GetAdminCasellaIdsAsync(utente);
            if (caselleAdmin == null || caselleAdmin.Count == 0)
                return (new List<EmailListItem_NEW>(), 0);

            using var conn = new OracleConnection(_connectionString);
            await conn.OpenAsync();

            // filtro word: SOLO OGGETTO + MITTENTE
            var w = (word ?? "").Trim().ToLowerInvariant();
            bool hasWord = !string.IsNullOrWhiteSpace(w) && w.Length >= 2;

            var inList = string.Join(",", caselleAdmin.Select(x => x.ToString()));

            // 1) COUNT separata (più veloce di COUNT(*) OVER() per pagina)
            var countSql = $@"
SELECT COUNT(*)
FROM SGAPP.EMAIL_RICEVUTE e
WHERE NVL(e.ELIMINATO,'N') = 'N'
  AND e.CASELLA_ID IN ({inList})
  AND NOT EXISTS (
        SELECT 1
        FROM SGAPP.EMAIL_ASSEGNAZIONI x
        WHERE x.EMAIL_ID = e.ID
  )
";

            if (hasWord)
            {
                countSql += @"
  AND (
        LOWER(NVL(e.OGGETTO,''))  LIKE '%' || :p_word || '%'
     OR LOWER(NVL(e.MITTENTE,'')) LIKE '%' || :p_word || '%'
  )
";
            }

            int total;
            await using (var countCmd = new OracleCommand(countSql, conn) { BindByName = true })
            {
                if (hasWord)
                    countCmd.Parameters.Add("p_word", OracleDbType.Varchar2).Value = w;

                var obj = await countCmd.ExecuteScalarAsync();
                total = Convert.ToInt32(obj);
            }

            if (total == 0)
                return (new List<EmailListItem_NEW>(), 0);

            // 2) Query paginata ottimizzata: prima ID (paged), poi join + allegati solo su quei 20 ID
            var sql = $@"
WITH base AS (
    SELECT e.ID, e.DATA_RICEZIONE
    FROM SGAPP.EMAIL_RICEVUTE e
    WHERE NVL(e.ELIMINATO,'N') = 'N'
      AND e.CASELLA_ID IN ({inList})
      AND NOT EXISTS (
            SELECT 1
            FROM SGAPP.EMAIL_ASSEGNAZIONI x
            WHERE x.EMAIL_ID = e.ID
      )
";

            if (hasWord)
            {
                sql += @"
      AND (
            LOWER(NVL(e.OGGETTO,''))  LIKE '%' || :p_word || '%'
         OR LOWER(NVL(e.MITTENTE,'')) LIKE '%' || :p_word || '%'
      )
";
            }

            sql += @"
),
paged AS (
    SELECT ID
    FROM base
    ORDER BY DATA_RICEZIONE DESC
    OFFSET :p_offset ROWS FETCH NEXT :p_limit ROWS ONLY
),
att AS (
    SELECT a.EMAIL_ID,
           1 AS HAS_ATTACH,
           LISTAGG(
              a.ID || '::' || a.NOME_FILE || '::' || NVL(a.MIME_TYPE,''),
              '||'
           ) WITHIN GROUP (ORDER BY a.ID) AS ATT_PACK
    FROM SGAPP.EMAIL_ALLEGATI a
    JOIN paged p ON p.ID = a.EMAIL_ID
    GROUP BY a.EMAIL_ID
)
SELECT
    e.ID,
    e.MITTENTE,
    e.DESTINATARI,
    e.OGGETTO,
    e.DATA_RICEZIONE,
    e.CASELLA_ID,
    c.EMAIL AS CASELLA_EMAIL,
    e.APERTO,
    e.CC,
    e.CCN,
    NVL(att.HAS_ATTACH,0) AS HAS_ATTACH,
    att.ATT_PACK,
    -- preview: se CORPO_* sono CLOB e vuoi ancora più veloce, dimmelo che ti faccio DBMS_LOB.SUBSTR
    SUBSTR(NVL(e.CORPO_TESTO, e.CORPO_HTML), 1, 200) AS PREVIEW
FROM paged p
JOIN SGAPP.EMAIL_RICEVUTE e ON e.ID = p.ID
JOIN SGAPP.CASELLEPOSTA c ON c.ID = e.CASELLA_ID
LEFT JOIN att ON att.EMAIL_ID = e.ID
ORDER BY e.DATA_RICEZIONE DESC
";

            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };

            if (hasWord)
                cmd.Parameters.Add("p_word", OracleDbType.Varchar2).Value = w;

            cmd.Parameters.Add("p_offset", OracleDbType.Int32).Value = start;
            cmd.Parameters.Add("p_limit", OracleDbType.Int32).Value = pageSize;

            var list = new List<EmailListItem_NEW>();

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var attPack = GetStr(reader, "ATT_PACK");

                List<AllegatoItem_NEW>? allegati = null;
                if (!string.IsNullOrWhiteSpace(attPack))
                {
                    allegati = attPack
                        .Split("||", StringSplitOptions.RemoveEmptyEntries)
                        .Select(x =>
                        {
                            var parts = x.Split("::");
                            var id = parts.Length > 0 ? int.Parse(parts[0]) : 0;
                            var nome = parts.Length > 1 ? parts[1] : "";
                            var mime = parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]) ? parts[2] : null;
                            return new AllegatoItem_NEW(id, nome, mime);
                        })
                        .Where(a => a.Id > 0)
                        .ToList();
                }

                list.Add(new EmailListItem_NEW(
                    Id: reader.GetInt32(reader.GetOrdinal("ID")),
                    Data: reader.IsDBNull(reader.GetOrdinal("DATA_RICEZIONE"))
                        ? (DateTime?)null
                        : reader.GetDateTime(reader.GetOrdinal("DATA_RICEZIONE")),
                    Mittente: GetStr(reader, "MITTENTE") ?? "",
                    Oggetto: GetStr(reader, "OGGETTO") ?? "(senza oggetto)",
                    Aperto: GetStr(reader, "APERTO") ?? "",
                    HasAttachments: reader.GetInt32(reader.GetOrdinal("HAS_ATTACH")) == 1,
                    ThreadLen: 1,
                    Replies: 0,
                    Preview: GetStr(reader, "PREVIEW"),
                    Allegati: allegati,
                    MessageId: null,
                    CasellaId: reader.IsDBNull(reader.GetOrdinal("CASELLA_ID"))
                        ? null
                        : reader.GetInt32(reader.GetOrdinal("CASELLA_ID")),
                    CasellaEmail: GetStr(reader, "CASELLA_EMAIL"),
                    AssegnatoA: null, // unassigned
                    Destinatari: GetStr(reader, "DESTINATARI"),
                    Cc: GetStr(reader, "CC"),
                    Ccn: GetStr(reader, "CCN")
                ));
            }

            return (list, total);
        }
        public async Task<List<int>> GetAdminCasellaIdsAsync(string utente)
        {
            using var conn = new OracleConnection(_connectionString);
            await conn.OpenAsync();

            const string sql = @"
SELECT DISTINCT ca.CASELLA_ID
FROM SGAPP.CASELLA_ABILITAZIONI ca
WHERE LOWER(ca.USERNAME) = LOWER(:p_utente)
  AND NVL(ca.IS_ADMIN, 0) = 1";

            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
            cmd.Parameters.Add("p_utente", OracleDbType.Varchar2).Value = utente;

            var ids = new List<int>();
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                ids.Add(r.GetInt32(0));

            return ids;
        }

        public async Task<int> CountUnassignedForAdminAsync(
    List<int> casellaIdsAdmin,
    string? word = null,
    string? address = null)
        {
            if (casellaIdsAdmin == null || casellaIdsAdmin.Count == 0) return 0;

            using var conn = new OracleConnection(_connectionString);
            await conn.OpenAsync();

            var w = (word ?? "").Trim().ToLowerInvariant();
            var a = (address ?? "").Trim().ToLowerInvariant();
            bool hasWord = w.Length >= 2;
            bool hasAddr = a.Length >= 2;

            var inList = string.Join(",", casellaIdsAdmin);

            var sql = $@"
SELECT COUNT(*)
FROM SGAPP.EMAIL_RICEVUTE e
WHERE NVL(e.ELIMINATO,'N') = 'N'
  AND e.CASELLA_ID IN ({inList})
  AND NOT EXISTS (
        SELECT 1
        FROM SGAPP.EMAIL_ASSEGNAZIONI x
        WHERE x.EMAIL_ID = e.ID
  )
";

            if (hasWord)
            {
                sql += @"
  AND (
        LOWER(NVL(e.OGGETTO,''))      LIKE '%' || :p_word || '%'
     OR LOWER(NVL(e.MITTENTE,''))     LIKE '%' || :p_word || '%'
     OR LOWER(NVL(e.DESTINATARI,''))  LIKE '%' || :p_word || '%'
     OR LOWER(NVL(e.CC,''))           LIKE '%' || :p_word || '%'
     OR LOWER(NVL(e.CCN,''))          LIKE '%' || :p_word || '%'
     OR EXISTS (
           SELECT 1
           FROM SGAPP.EMAIL_ALLEGATI al
           WHERE al.EMAIL_ID = e.ID
             AND LOWER(NVL(al.NOME_FILE,'')) LIKE '%' || :p_word || '%'
     )
  )
";
            }

            if (hasAddr)
            {
                sql += @"
  AND (
        LOWER(NVL(e.MITTENTE,''))     LIKE '%' || :p_addr || '%'
     OR LOWER(NVL(e.DESTINATARI,'')) LIKE '%' || :p_addr || '%'
     OR LOWER(NVL(e.CC,''))          LIKE '%' || :p_addr || '%'
     OR LOWER(NVL(e.CCN,''))         LIKE '%' || :p_addr || '%'
  )
";
            }

            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
            if (hasWord) cmd.Parameters.Add("p_word", OracleDbType.Varchar2).Value = w;
            if (hasAddr) cmd.Parameters.Add("p_addr", OracleDbType.Varchar2).Value = a;

            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }

        private static string SanitizeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            name = name.Trim();
            return string.IsNullOrWhiteSpace(name) ? "allegato" : name;
        }

        private static string EnsureUniquePath(string fullPath)
        {
            if (!File.Exists(fullPath)) return fullPath;

            var dir = Path.GetDirectoryName(fullPath)!;
            var file = Path.GetFileNameWithoutExtension(fullPath);
            var ext = Path.GetExtension(fullPath);

            for (int i = 1; i < 10000; i++)
            {
                var candidate = Path.Combine(dir, $"{file}_{i}{ext}");
                if (!File.Exists(candidate)) return candidate;
            }
            throw new IOException("Impossibile trovare un nome file univoco.");
        }

        private static async Task<string> SaveSentAttachmentAsync(
            string basePath,
            long casellaId,
            long emailId,
            string originalFileName,
            byte[] bytes,
            CancellationToken ct)
        {
            var dir = Path.Combine(basePath, "inviati", casellaId.ToString(), emailId.ToString());
            Directory.CreateDirectory(dir);

            var safeName = SanitizeFileName(originalFileName);
            var fullPath = EnsureUniquePath(Path.Combine(dir, safeName));

            await File.WriteAllBytesAsync(fullPath, bytes, ct);

            return Path.GetRelativePath(basePath, fullPath); // es: inviati\12\345\file.pdf
        }
    }



    }
