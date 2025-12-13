using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MimeKit;
using MudBlazor.Charts;
using Oracle.ManagedDataAccess.Client;
using SCemail.Components.Data;
using SCemail.Components.Shared;
using System.Data;
using System.Linq;
using System.Text.RegularExpressions;
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


        public MailService_NEW(IDbContextFactory<MailDbContext> dbFactory,
                           ILogger<MailService_NEW> logger,
                           HttpClient http, IConfiguration config, AccessiService accessiService
                           )
        {
            _dbFactory = dbFactory;
            _logger = logger;
            _http = http;
            _connectionString = config.GetConnectionString("OracleDb")
                ?? throw new InvalidOperationException("Connection string 'OracleDb' mancante nel file di configurazione.");
            _accessiService = accessiService;
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

        public async Task SegnaMenzioneVistaAsync(int emailId, string utente)
        {
            using var db = _dbFactory.CreateDbContext();

            var rows = await db.emailMenzionis
                .Where(m => m.EmailId == emailId && m.Utente == utente && m.Visto == "N")
                .ToListAsync();

            foreach (var row in rows)
                row.Visto = "Y";

            await db.SaveChangesAsync();
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
                    Mittente: reader.GetString("MITTENTE"),
                    Oggetto: reader.GetString("OGGETTO"),
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

        public async Task<EmailReplyInfo?> GetReplyInfoAsync(
  string? threadKey,
  string? inReplyTo)
        {
            if (string.IsNullOrWhiteSpace(threadKey) && string.IsNullOrWhiteSpace(inReplyTo))
                return null;

            using var conn = new OracleConnection(_connectionString);
            await conn.OpenAsync();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
            SELECT
                ci.EMAIL AS CASELLA_MITTENTE,
                ei.DESTINATARI,
                ei.CC,
                ei.BCC
            FROM SGAPP.EMAIL_INVIATE ei
            JOIN SGAPP.CASELLEPOSTA ci ON ci.ID = ei.CASELLA_ID
            WHERE ei.THREAD_KEY = :threadKey
               OR ei.MESSAGE_ID = :inReplyTo
            ORDER BY ei.DATA_INVIO DESC
            FETCH FIRST 1 ROWS ONLY";

            cmd.Parameters.Add("threadKey", threadKey);
            cmd.Parameters.Add("inReplyTo", inReplyTo);

            using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return null;

            return new EmailReplyInfo
            {
                CasellaMittente = reader.GetString(0),
                Destinatari = reader.GetString(1),
                Cc = reader.IsDBNull(2) ? null : reader.GetString(2),
                Bcc = reader.IsDBNull(3) ? null : reader.GetString(3)
            };
        }

        public record CommentoEmail(int Id, string Testo, DateTime DataCreazione, string Autore);

        public async Task<List<AdminEmailDto>> GetEmailInLavorazioneAsync()
        {
            using var db = _dbFactory.CreateDbContext();

            var query =
                from a in db.EmailAssegnazione
                join e in db.EmailRicevute on a.EmailId equals e.Id
                where !db.EmailArchivio.Any(ar => ar.IdEmail == e.Id)
                select new
                {
                    e.Id,
                    e.Oggetto,
                    e.Mittente,
                    e.DataRicezione,
                    a.Utente
                };

            var raw = await query.OrderByDescending(x => x.DataRicezione).ToListAsync();

            // 👉 Qui aggiungiamo la proprietà Completata *in memoria* (NO SQL)
            return raw.Select(x => new AdminEmailDto
            {
                EmailId = x.Id,
                Oggetto = x.Oggetto,
                Mittente = x.Mittente,
                Data = x.DataRicezione,
                AssegnatoA = x.Utente,
                Completata = false   // ← questo ora NON finisce in SQL
            }).ToList();
        }



        public async Task<List<AdminEmailDto>> GetEmailCompletateAsync()
        {
            using var db = _dbFactory.CreateDbContext();

            var query =
                from ar in db.EmailArchivio
                join e in db.EmailRicevute on ar.IdEmail equals e.Id
                select new
                {
                    e.Id,
                    e.Oggetto,
                    e.Mittente,
                    ar.DataArchiviazione,
                    ar.Utente
                };

            var raw = await query.OrderByDescending(x => x.DataArchiviazione).ToListAsync();

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
        SELECT *
          FROM (
                SELECT DISTINCT LOWER(TRIM(destinatario)) AS email
                  FROM sgapp.email_destinatari
                 WHERE LOWER(destinatario) LIKE :pattern
                   AND destinatario LIKE '%@%'
               )
         WHERE ROWNUM <= :max";

            await using var cmd = new OracleCommand(sql, conn);
            cmd.BindByName = true;
            cmd.Parameters.Add("pattern", OracleDbType.Varchar2).Value = query.ToLower() + "%";
            cmd.Parameters.Add("max", OracleDbType.Int32).Value = max;

            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                var email = rdr.GetString(0)?.Trim();
                if (!string.IsNullOrEmpty(email))
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


        public async Task AssignEmailAsync(int emailId, string utente, bool soloInvio, string? commento = null)
        {
            await using var conn = new OracleConnection(_connectionString);
            await conn.OpenAsync();

            // 1️⃣ Inserisci l'assegnazione
            const string sqlAssegna = @"
        INSERT INTO EMAIL_ASSEGNAZIONI (EMAIL_ID, UTENTE, SOLO_INVIO)
        VALUES (:p_eid, :p_user, :p_solo)";

            await using (var cmd = new OracleCommand(sqlAssegna, conn) { BindByName = true })
            {
                cmd.Parameters.Add("p_eid", OracleDbType.Int32).Value = emailId;
                cmd.Parameters.Add("p_user", OracleDbType.Varchar2).Value = utente;
                cmd.Parameters.Add("p_solo", OracleDbType.Char).Value = soloInvio ? "Y" : "N";
                await cmd.ExecuteNonQueryAsync();
            }

            // 2️⃣ Se c’è un commento, inseriscilo nella tabella EMAIL_COMMENTI
            if (!string.IsNullOrWhiteSpace(commento))
            {
                const string sqlCommento = @"
            INSERT INTO COMMENTI_EMAIL (EMAIL_ID, AUTORE, TESTO, DATA_CREAZIONE)
            VALUES (:p_eid, :p_autore, :p_testo, SYSDATE)";

                await using var cmd2 = new OracleCommand(sqlCommento, conn) { BindByName = true };
                cmd2.Parameters.Add("p_eid", OracleDbType.Int32).Value = emailId;
                cmd2.Parameters.Add("p_autore", OracleDbType.Varchar2).Value = utente;
                cmd2.Parameters.Add("p_testo", OracleDbType.Clob).Value = commento;
                await cmd2.ExecuteNonQueryAsync();
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
        public async Task<List<string>> GetUserListAsync(CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();

            var allUsers = await db.InfoUsers
                .AsNoTracking()
                .Select(u => u.Utente)
                .OrderBy(u => u)
                .ToListAsync(ct);

            // 🔥 FILTRO client-side
            var filtered = allUsers
                .Where(u => Regex.IsMatch(u, @"^[a-z]\.[a-z]+$", RegexOptions.IgnoreCase))
                .ToList();

            return filtered;
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
    CASE WHEN EXISTS (
        SELECT 1 FROM SGAPP.EMAIL_ALLEGATI a WHERE a.EMAIL_ID = e.ID
    ) THEN 1 ELSE 0 END AS HAS_ATTACH,
    (
        SELECT LISTAGG(a.NOME_FILE, '||') WITHIN GROUP (ORDER BY a.ID)
        FROM SGAPP.EMAIL_ALLEGATI a
        WHERE a.EMAIL_ID = e.ID
    ) AS ATT_NAMES,
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

            // --- filtro di ricerca ---
            if (!string.IsNullOrWhiteSpace(filtro))
            {
                baseSql += @"
        AND (
              LOWER(NVL(e.OGGETTO,''))       LIKE :filtro
           OR LOWER(NVL(e.MITTENTE,''))      LIKE :filtro
           OR LOWER(NVL(e.DESTINATARI,''))   LIKE :filtro
           OR LOWER(NVL(e.CORPO_TESTO,''))   LIKE :filtro
           OR LOWER(NVL(e.CORPO_HTML,''))    LIKE :filtro
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
                    string? attNames = reader.IsDBNull("ATT_NAMES") ? null : reader.GetString("ATT_NAMES");

                    List<AllegatoItem_NEW>? allegati = null;
                    if (!string.IsNullOrEmpty(attNames))
                    {
                        allegati = attNames
                            .Split("||", StringSplitOptions.RemoveEmptyEntries)
                            .Select((nf, i) => new AllegatoItem_NEW(-1 - i, nf, null))
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
AssegnatoA: reader.IsDBNull("ASSEGNATO_A") ? null : reader.GetString("ASSEGNATO_A") 

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



        public async Task<int> SaveDraftAsync(
    string utente,
    string? destinatari,
    string? oggetto,
    string? corpoHtml,
    List<OutgoingAttachment_NEW>? allegati = null,
    CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();

            var bozza = new EmailBozza
            {
                Utente = utente,
                Destinatari = destinatari,
                Oggetto = oggetto,
                CorpoHtml = corpoHtml,
                LastSaved = DateTime.Now
            };

            db.EmailBozze.Add(bozza);
            await db.SaveChangesAsync(ct);

            if (allegati != null && allegati.Any())
            {
                foreach (var a in allegati)
                {
                    db.BozzaAllegati.Add(new BozzaAllegato
                    {
                        BozzaId = bozza.Id,
                        NomeFile = a.FileName,
                        MimeType = a.MimeType,
                        Content = a.Content
                    });
                }
                await db.SaveChangesAsync(ct);
            }

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
                    using var ms = new MemoryStream(a.Content);
                    builder.Attachments.Add(a.FileName, ms, ContentType.Parse(a.MimeType ?? "application/octet-stream"));
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
                        Content = a.Content
                    });
                }
            }

            db.EmailInviate.Add(inviata);
            await db.SaveChangesAsync(ct);

            _logger.LogInformation("💾 Email inviata salvata ID={Id}, MessageId={MsgId}, ThreadKey={ThreadKey}",
                inviata.Id, inviata.MessageId, inviata.ThreadKey);


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


        public async Task<List<string>> GetRecentRecipientsAsync(int max = 10, CancellationToken ct = default)
        {
            var list = new List<string>();
            await using var conn = new OracleConnection(_connectionString);
            await conn.OpenAsync(ct);

            const string sql = @"
        SELECT email
          FROM (
                SELECT DISTINCT LOWER(TRIM(destinatario)) AS email
                  FROM sgapp.email_destinatari
                 WHERE destinatario LIKE '%@%'
               )
         WHERE ROWNUM <= :max";

            await using var cmd = new OracleCommand(sql, conn);
            cmd.BindByName = true;
            cmd.Parameters.Add("max", OracleDbType.Int32).Value = max;

            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                var email = rdr.GetString(0)?.Trim();
                if (!string.IsNullOrEmpty(email))
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


    }
}
