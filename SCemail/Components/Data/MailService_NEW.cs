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
using Oracle.ManagedDataAccess.Types;
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
        private readonly IConfiguration _config;
        private readonly IOptions<AttachmentsOptions> _attachmentsOpt;
        private readonly string _attachmentsBasePath;
        public MailService_NEW(
            IDbContextFactory<MailDbContext> dbFactory,
            ILogger<MailService_NEW> logger,
            HttpClient http,
            IConfiguration config,
            AccessiService accessiService,
            IOptions<AttachmentsOptions> attachmentsOpt)
        {
            _dbFactory = dbFactory;
            _logger = logger;
            _http = http;
            _config = config;
            _connectionString = config.GetConnectionString("OracleDb")
                ?? throw new InvalidOperationException("Connection string 'OracleDb' mancante nel file di configurazione.");
            _accessiService = accessiService;
            _attachmentsOpt = attachmentsOpt;
            _attachmentsBasePath = _config.GetValue<string>("Attachments:BasePath")
        ?? Path.Combine(AppContext.BaseDirectory, "attachments");

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
            if (string.IsNullOrWhiteSpace(utente))
                return (new List<EmailListItem_NEW>(), 0);

            await using var db = _dbFactory.CreateDbContext();

            var utenteNorm = utente.Trim().ToLowerInvariant();

            // 1) prendo TUTTE le menzioni dell'utente corrente, viste e non viste
            var mentionsBase = await (
                from m in db.emailMenzionis.AsNoTracking()
                join e in db.EmailRicevute.AsNoTracking() on m.EmailId equals e.Id
                where m.Utente != null
                      && m.Utente.ToLower() == utenteNorm
                      && (e.Eliminato == null || e.Eliminato != "Y")
                      && !db.EmailArchivio.Any(a =>
                            a.IdEmail == m.EmailId &&
                            a.Utente != null &&
                            a.Utente.ToLower() == utenteNorm)
                select new
                {
                    m.EmailId,
                    Menzionato = m.Utente,
                    m.Visto,
                    m.DataMenzione,

                    e.Id,
                    e.ThreadKey,
                    e.DataRicezione,
                    e.Mittente,
                    e.Oggetto,
                    e.CorpoHtml,
                    e.CorpoTesto,
                    e.CasellaId,
                    e.Destinatari
                }
            ).ToListAsync();

            if (mentionsBase.Count == 0)
                return (new List<EmailListItem_NEW>(), 0);

            // 2) group per thread
            var threadGroups = mentionsBase
                .GroupBy(x => string.IsNullOrWhiteSpace(x.ThreadKey)
                    ? $"SINGLE_{x.Id}"
                    : x.ThreadKey!.Trim())
                .Select(g =>
                {
                    var orderedEmails = g
                        .OrderByDescending(x => x.DataRicezione)
                        .ThenByDescending(x => x.Id)
                        .ToList();

                    var lastMention = g
                        .OrderByDescending(x => x.DataMenzione)
                        .ThenByDescending(x => x.Id)
                        .First();

                    var unreadMentionCount = g.Count(x => x.Visto == "N");

                    return new
                    {
                        ThreadKey = g.Key,
                        LastMentionDate = lastMention.DataMenzione,
                        MentionedUser = utenteNorm,
                        UnreadMentionCount = unreadMentionCount,
                        IsReadMention = unreadMentionCount == 0,
                        ThreadLen = g.Select(x => x.Id).Distinct().Count(),
                        LastEmailId = orderedEmails.First().Id,
                        EmailIds = g.Select(x => x.Id).Distinct().ToList()
                    };
                })
                .OrderByDescending(x => x.LastMentionDate)
                .ToList();

            var total = threadGroups.Count;

            var pageKeys = threadGroups
                .Skip(start)
                .Take(pageSize)
                .ToList();

            if (pageKeys.Count == 0)
                return (new List<EmailListItem_NEW>(), total);

            var lastEmailIds = pageKeys
                .Select(x => x.LastEmailId)
                .Distinct()
                .ToList();
            var assegnazioni = await db.EmailAssegnazione
                    .AsNoTracking()
                    .Where(a => lastEmailIds.Contains(a.EmailId)
                             && (a.SoloInvio == null || a.SoloInvio != "Y"))
                    .Select(a => new
                    {
                        a.EmailId,
                        a.Utente
                    })
                    .ToListAsync();

            var assegnatiMap = assegnazioni
                .GroupBy(a => a.EmailId)
                .ToDictionary(
                    g => g.Key,
                    g => string.Join("; ", g
                        .Select(x => x.Utente)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct()
                        .OrderBy(x => x))
                );
            // 3) email latest della pagina
            var emails = await (
                from e in db.EmailRicevute.AsNoTracking()
                join c in db.CasellePosta.AsNoTracking() on e.CasellaId equals c.Id into caselle
                from casella in caselle.DefaultIfEmpty()
                where lastEmailIds.Contains(e.Id)
                select new
                {
                    e.Id,
                    e.ThreadKey,
                    e.DataRicezione,
                    e.Mittente,
                    e.Oggetto,
                    e.CorpoHtml,
                    e.CorpoTesto,
                    e.CasellaId,
                    CasellaEmail = casella != null ? casella.Email : null,
                    e.Destinatari
                }
            ).ToListAsync();

            // 4) commenti del thread per capire se il menzionato ha risposto
            var pageThreadEmailIds = pageKeys
                .SelectMany(x => x.EmailIds)
                .Distinct()
                .ToList();

            var threadComments = await db.CommentiEmail
                .AsNoTracking()
                .Where(c => pageThreadEmailIds.Contains(c.EmailId))
                .Select(c => new
                {
                    c.EmailId,
                    c.Autore,
                    c.DataCreazione
                })
                .ToListAsync();

            // 5) allegati
            var allegati = await db.EmailAllegati
                .AsNoTracking()
                .Where(a => lastEmailIds.Contains(a.EmailId))
                .Select(a => new
                {
                    a.Id,
                    a.EmailId,
                    a.NomeFile,
                    a.MimeType
                })
                .ToListAsync();

            var keyMap = pageKeys.ToDictionary(x => x.LastEmailId, x => x);

            var result = emails
                .OrderByDescending(e => keyMap[e.Id].LastMentionDate)
                .Select(e =>
                {
                    var k = keyMap[e.Id];

                    var preview = !string.IsNullOrWhiteSpace(e.CorpoTesto)
                        ? e.CorpoTesto
                        : e.CorpoHtml;

                    if (!string.IsNullOrEmpty(preview) && preview.Length > 200)
                        preview = preview[..200];

                    var mentionedUserHasReplied = threadComments.Any(c =>
                        k.EmailIds.Contains(c.EmailId) &&
                        c.Autore != null &&
                        c.Autore.ToLower() == utenteNorm &&
                        c.DataCreazione > k.LastMentionDate
                    );

                    var allegatiMail = allegati
                        .Where(a => a.EmailId == e.Id)
                        .Select(a => new AllegatoItem_NEW(
                            a.Id,
                            a.NomeFile,
                            a.MimeType
                        ))
                        .ToList();

                    return new EmailListItem_NEW(
                        Id: e.Id,
                        Data: e.DataRicezione,
                        Mittente: e.Mittente ?? "",
                        Oggetto: e.Oggetto ?? "",
                        Aperto: k.IsReadMention ? "Y" : "N",
                        HasAttachments: allegatiMail.Any(),
                        ThreadLen: k.ThreadLen <= 0 ? 1 : k.ThreadLen,
                        Replies: Math.Max(0, (k.ThreadLen <= 0 ? 1 : k.ThreadLen) - 1),
                        Preview: preview,
                        Allegati: allegatiMail,
                        MessageId: null,
                        CasellaId: e.CasellaId,
                        CasellaEmail: e.CasellaEmail,
                        ThreadKey: string.IsNullOrWhiteSpace(e.ThreadKey) ? null : e.ThreadKey,
                        AssegnatoA: assegnatiMap.TryGetValue(e.Id, out var ass)
                            ? ass
                            : "", Destinatari: e.Destinatari,
                        LettoSeguita: null,
                        LettoSeguitaIl: null,
                        CanArchive: mentionedUserHasReplied,
                        IsReadByCurrentUser: k.IsReadMention
                    );
                })
                .ToList();

            return (result, total);
        }

        public async Task MarkEmailSeguitaAsReadAsync(
    int emailId,
    string utente,
    CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(utente))
                return;

            await using var db = _dbFactory.CreateDbContext();

            var utenteNorm = utente.Trim().ToLowerInvariant();

            var row = await db.EmailSeguite
                .FirstOrDefaultAsync(x => x.EmailId == emailId && x.Utente == utenteNorm, ct);

            if (row == null)
                return;

            row.Letto = "Y";
            row.LettoIl = DateTime.Now;

            await db.SaveChangesAsync(ct);
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

        public async Task<(int TotalInbox, int UnreadInbox)> GetInboxHomeCountsAsync(string utente)
        {
            await using var db = _dbFactory.CreateDbContext();

            var utenteNorm = utente.Trim().ToLower();
            var minDate = GetMailUiMinDate();

            var rows = await (
                from e in db.EmailRicevute.AsNoTracking()
                join a in db.EmailAssegnazione.AsNoTracking()
                    on e.Id equals a.EmailId
                where a.Utente != null
                      && a.Utente.ToLower() == utenteNorm
                      && (e.Eliminato == null || e.Eliminato != "Y")
                      && (e.Blacklist == null || e.Blacklist != "Y")
                      && (!minDate.HasValue || e.DataRicezione >= minDate.Value)
                      && !db.EmailArchivio.Any(ar =>
                            ar.IdEmail == e.Id &&
                            ar.Utente != null &&
                            ar.Utente.ToLower() == utenteNorm)
                      && !db.EmailInboxSezioneMap.Any(m =>
                            m.IdEmail == e.Id &&
                            m.Utente != null &&
                            m.Utente.ToLower() == utenteNorm)
                select new
                {
                    e.Id,
                    e.ThreadKey,

                    LettureCount = db.EmailLetture.Count(l =>
                        l.EmailId == e.Id &&
                        l.Utente != null &&
                        l.Utente.ToLower() == utenteNorm)
                }
            ).ToListAsync();

            var grouped = rows
                .GroupBy(x => string.IsNullOrWhiteSpace(x.ThreadKey)
                    ? $"SINGLE_{x.Id}"
                    : x.ThreadKey.Trim())
                .ToList();

            return (
                TotalInbox: grouped.Count,
                UnreadInbox: grouped.Count(g => g.Any(x => x.LettureCount == 0))
            );
        }
        public async Task<(List<EmailListItem_NEW> Page, int Total)> GetSpamEmailsPagedAsync(
    int start,
    int pageSize,
    string? word = null,
    string? address = null)
        {
            await using var db = _dbFactory.CreateDbContext();

            var q = db.EmailRicevute
                .AsNoTracking()
                .Include(e => e.Casella)
                .Include(e => e.Allegati)
                .Where(e =>
                    e.Blacklist == "Y" &&
                    (e.Eliminato == null || e.Eliminato != "Y"));

            if (!string.IsNullOrWhiteSpace(word) && word.Length >= 2)
            {
                var w = word.Trim().ToLower();

                q = q.Where(e =>
                    (e.Oggetto != null && e.Oggetto.ToLower().Contains(w)) ||
                    (e.Mittente != null && e.Mittente.ToLower().Contains(w)) ||
                    (e.CorpoTesto != null && e.CorpoTesto.ToLower().Contains(w)));
            }

            if (!string.IsNullOrWhiteSpace(address))
            {
                var a = address.Trim().ToLower();

                q = q.Where(e =>
                    (e.Mittente != null && e.Mittente.ToLower().Contains(a)) ||
                    (e.Destinatari != null && e.Destinatari.ToLower().Contains(a)));
            }

            var total = await q.CountAsync();

            var raw = await q
                .OrderByDescending(e => e.DataRicezione)
                .Skip(start)
                .Take(pageSize)
                .ToListAsync();

            var page = raw.Select(e => new EmailListItem_NEW(
                Id: e.Id,
                Data: e.DataRicezione,
                Mittente: e.Mittente,
                Oggetto: e.Oggetto,
                Aperto: e.Aperto,
                HasAttachments: e.Allegati.Any(),
                ThreadLen: 1,
                Replies: 0,
                Preview: e.CorpoTesto ?? e.CorpoHtml,
                Allegati: e.Allegati.Select(a => new AllegatoItem_NEW
                {
                    Id = a.Id,
                    NomeFile = a.NomeFile,
                    MimeType = a.MimeType
                }).ToList(),
                MessageId: e.MessageId,
                CasellaId: e.CasellaId,
                CasellaEmail: e.Casella.Email,
                ThreadKey: e.ThreadKey,
                Destinatari: e.Destinatari,
                IsReadByCurrentUser: e.Aperto == "Y"
            )).ToList();

            return (page, total);
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
        private DateTime? GetMailUiMinDate()
        {
            var raw = _config["MailUi:MinDate"];

            if (string.IsNullOrWhiteSpace(raw))
                return null;

            if (DateTime.TryParse(raw, out var dt))
                return dt.Date;

            return null;
        }

        public async Task<List<EmailDetail_NEW>> GetConversationByThreadAsync(int emailId, string? utente = null)
        {
            await using var conn = await GetOpenConnectionAsync();

            const string sql = @"
WITH base AS (
    SELECT 0 AS SRC_PRIORITY, THREAD_KEY, MESSAGE_ID, IN_REPLY_TO, REFERENCES_HDR
    FROM SGAPP.EMAIL_RICEVUTE
    WHERE ID = :id

    UNION ALL

    SELECT 1 AS SRC_PRIORITY, THREAD_KEY, MESSAGE_ID, IN_REPLY_TO, REFERENCES_HDR
    FROM SGAPP.EMAIL_INVIATE
    WHERE ID = :id
),
base_one AS (
    SELECT
        TRIM(THREAD_KEY) AS THREAD_KEY,
        TRIM(MESSAGE_ID) AS MESSAGE_ID,
        TRIM(IN_REPLY_TO) AS IN_REPLY_TO,
        REFERENCES_HDR
    FROM (
        SELECT SRC_PRIORITY, THREAD_KEY, MESSAGE_ID, IN_REPLY_TO, REFERENCES_HDR
        FROM base
        ORDER BY SRC_PRIORITY
    )
    WHERE ROWNUM = 1
),
raw_thread AS (
    SELECT 
        r.ID,
        r.CASELLA_ID,
        TO_CLOB(cp.EMAIL) AS CASELLA_EMAIL,
        TO_CLOB(r.MITTENTE) AS MITTENTE,
        TO_CLOB(r.DESTINATARI) AS DESTINATARI,
        TO_CLOB(r.CC) AS CC,
        TO_CLOB(r.CCN) AS CCN,
        TO_CLOB(r.OGGETTO) AS OGGETTO,
        r.DATA_RICEZIONE AS DATA,
        TO_CLOB(r.CORPO_HTML) AS CORPO_HTML,
        TO_CLOB(r.CORPO_TESTO) AS CORPO_TESTO,
        TO_CLOB(r.MESSAGE_ID) AS MESSAGE_ID,
        TO_CLOB(r.IN_REPLY_TO) AS IN_REPLY_TO,
        TO_CLOB(r.REFERENCES_HDR) AS REFERENCES_HDR,
        TO_CLOB(r.THREAD_KEY) AS THREAD_KEY,
        'R' AS TIPO
    FROM SGAPP.EMAIL_RICEVUTE r
    LEFT JOIN SGAPP.CASELLEPOSTA cp ON cp.ID = r.CASELLA_ID
        CROSS JOIN base_one b
    WHERE NVL(r.ELIMINATO, 'N') = 'N'
            AND (
                        (
                                b.THREAD_KEY IS NOT NULL
                                AND b.THREAD_KEY <> ''
                                AND UPPER(TRIM(r.THREAD_KEY)) = UPPER(b.THREAD_KEY)
                        )
                 OR (
                                (b.THREAD_KEY IS NULL OR b.THREAD_KEY = '')
                                AND (
                                             UPPER(TRIM(r.MESSAGE_ID)) = UPPER(b.MESSAGE_ID)
                                        OR UPPER(TRIM(r.IN_REPLY_TO)) = UPPER(b.MESSAGE_ID)
                                        OR UPPER(TRIM(r.MESSAGE_ID)) = UPPER(b.IN_REPLY_TO)
                                        OR INSTR(UPPER(NVL(r.REFERENCES_HDR, '')), UPPER(NVL(b.MESSAGE_ID, ''))) > 0
                                        OR INSTR(UPPER(NVL(r.REFERENCES_HDR, '')), UPPER(NVL(b.IN_REPLY_TO, ''))) > 0
                                        OR INSTR(UPPER(NVL(b.REFERENCES_HDR, '')), UPPER(TRIM(NVL(r.MESSAGE_ID, '')))) > 0
                                        OR INSTR(UPPER(NVL(b.REFERENCES_HDR, '')), UPPER(TRIM(NVL(r.IN_REPLY_TO, '')))) > 0
                                )
                        )
            )

    UNION ALL

    SELECT 
        i.ID,
        CAST(NULL AS NUMBER) AS CASELLA_ID,
        TO_CLOB(i.UTENTE) AS CASELLA_EMAIL,
        TO_CLOB(i.UTENTE) AS MITTENTE,
        TO_CLOB(i.DESTINATARI) AS DESTINATARI,
        TO_CLOB(i.CC) AS CC,
        TO_CLOB(i.BCC) AS CCN,
        TO_CLOB(i.OGGETTO) AS OGGETTO,
        i.DATA_INVIO AS DATA,
        TO_CLOB(i.CORPO_HTML) AS CORPO_HTML,
        TO_CLOB(i.CORPO_TESTO) AS CORPO_TESTO,
        TO_CLOB(i.MESSAGE_ID) AS MESSAGE_ID,
        TO_CLOB(i.IN_REPLY_TO) AS IN_REPLY_TO,
        TO_CLOB(i.REFERENCES_HDR) AS REFERENCES_HDR,
        TO_CLOB(i.THREAD_KEY) AS THREAD_KEY,
        'I' AS TIPO
    FROM SGAPP.EMAIL_INVIATE i
    CROSS JOIN base_one b
    WHERE (
            (
                b.THREAD_KEY IS NOT NULL
                AND b.THREAD_KEY <> ''
                AND UPPER(TRIM(i.THREAD_KEY)) = UPPER(b.THREAD_KEY)
            )
         OR (
                (b.THREAD_KEY IS NULL OR b.THREAD_KEY = '')
                AND (
                       UPPER(TRIM(i.MESSAGE_ID)) = UPPER(b.MESSAGE_ID)
                    OR UPPER(TRIM(i.IN_REPLY_TO)) = UPPER(b.MESSAGE_ID)
                    OR UPPER(TRIM(i.MESSAGE_ID)) = UPPER(b.IN_REPLY_TO)
                    OR INSTR(UPPER(NVL(i.REFERENCES_HDR, '')), UPPER(NVL(b.MESSAGE_ID, ''))) > 0
                    OR INSTR(UPPER(NVL(i.REFERENCES_HDR, '')), UPPER(NVL(b.IN_REPLY_TO, ''))) > 0
                    OR INSTR(UPPER(NVL(b.REFERENCES_HDR, '')), UPPER(TRIM(NVL(i.MESSAGE_ID, '')))) > 0
                    OR INSTR(UPPER(NVL(b.REFERENCES_HDR, '')), UPPER(TRIM(NVL(i.IN_REPLY_TO, '')))) > 0
                )
            )
    )
)
SELECT *
FROM raw_thread
ORDER BY DATA, ID";

            await using var cmd = new OracleCommand(sql, conn);
            cmd.BindByName = true;
            cmd.Parameters.Add("id", OracleDbType.Int32).Value = emailId;

            var list = new List<EmailDetail_NEW>();

            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                list.Add(new EmailDetail_NEW
                {
                    Id = reader.GetInt32(0),
                    CasellaId = reader.IsDBNull(1) ? null : reader.GetInt32(1),
                    CasellaEmail = GetStr(reader, 2),
                    Mittente = GetStr(reader, 3),
                    Destinatari = GetStr(reader, 4),
                    Cc = GetStr(reader, 5),
                    Ccn = GetStr(reader, 6),
                    Oggetto = GetStr(reader, 7),
                    Data = reader.IsDBNull(8) ? null : reader.GetDateTime(8),
                    CorpoHtml = GetStr(reader, 9),
                    CorpoTesto = GetStr(reader, 10),
                    MessageId = GetStr(reader, 11),
                    InReplyTo = GetStr(reader, 12),
                    References = GetStr(reader, 13),
                    ThreadKey = GetStr(reader, 14),
                    Tipo = GetStr(reader, 15),
                    IsLoaded = true
                });
            }

            const string attachSqlRicevute = @"
SELECT ID, NOME_FILE, MIME_TYPE, IS_EMAIL_EML, EMAIL_EML_ID
FROM SGAPP.EMAIL_ALLEGATI
WHERE EMAIL_ID = :id_email";

            const string attachSqlInviate = @"
SELECT ID, NOME_FILE, MIME_TYPE,
       'N' AS IS_EMAIL_EML,
       CAST(NULL AS NUMBER) AS EMAIL_EML_ID
FROM SGAPP.INVIATA_ALLEGATI
WHERE EMAIL_ID = :id_email";

            foreach (var mail in list)
            {
                mail.Allegati = new List<AllegatoItem_NEW>();

                var sqlAllegati = mail.Tipo == "I" ? attachSqlInviate : attachSqlRicevute;

                await using var aCmd = new OracleCommand(sqlAllegati, conn);
                aCmd.BindByName = true;
                aCmd.Parameters.Add("id_email", OracleDbType.Int32).Value = mail.Id;

                await using var aReader = await aCmd.ExecuteReaderAsync();

                while (await aReader.ReadAsync())
                {
                    mail.Allegati.Add(new AllegatoItem_NEW
                    {
                        Id = aReader.GetInt32(0),
                        NomeFile = aReader.IsDBNull(1) ? "" : aReader.GetString(1),
                        MimeType = aReader.IsDBNull(2) ? null : aReader.GetString(2),
                        IsEmailEml = !aReader.IsDBNull(3)
                            && string.Equals(aReader.GetString(3), "Y", StringComparison.OrdinalIgnoreCase),
                        EmailEmlId = aReader.IsDBNull(4) ? null : aReader.GetInt32(4)
                    });
                }
            }

              return list
                  .GroupBy(x => $"{(x.Tipo ?? "").Trim().ToUpperInvariant()}|{x.Id}")
                 .Select(g => g
                     .OrderByDescending(x => x.Allegati?.Count ?? 0)
                     .ThenBy(x => x.Data)
                     .ThenBy(x => x.Id)
                     .First())
                 .OrderBy(x => x.Data)
                 .ToList();
        }

        private static string BuildThreadDedupKey(EmailDetail_NEW x)
        {
            var subject = NormalizeSubjectForThread(x.Oggetto);
            var sender = NormalizeEmailForThread(x.Mittente);
            var recipients = NormalizeEmailForThread(x.Destinatari);
            var visibleBody = NormalizeBodyForThreadDedup(x.CorpoHtml ?? x.CorpoTesto);

            if (!string.IsNullOrWhiteSpace(visibleBody))
            {
                if (visibleBody.Length > 700)
                    visibleBody = visibleBody[..700];

                return $"{sender}|{recipients}|{subject}|{visibleBody}";
            }

            if (!string.IsNullOrWhiteSpace(x.MessageId))
                return x.MessageId.Trim().ToLowerInvariant();

            return $"{x.Tipo}-{x.Id}";
        }

        private static string NormalizeSubjectForThread(string? s)
        {
            s ??= "";
            s = Regex.Replace(s, @"^\s*((re|fwd|fw)\s*:\s*)+", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s+", " ");
            return s.Trim().ToLowerInvariant();
        }

        private static string NormalizeEmailForThread(string? s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return "";

            s = System.Net.WebUtility.HtmlDecode(s);

            var emails = Regex.Matches(s, @"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}")
                .Select(m => m.Value.Trim().ToLowerInvariant())
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            if (emails.Any())
                return string.Join(";", emails);

            s = Regex.Replace(s, @"\s+", " ");
            return s.Trim().ToLowerInvariant();
        }

        private static string NormalizeBodyForThreadDedup(string? htmlOrText)
        {
            if (string.IsNullOrWhiteSpace(htmlOrText))
                return "";

            var s = htmlOrText;

            s = Regex.Replace(s, @"<!--[\s\S]*?-->", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<style[\s\S]*?</style>", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<script[\s\S]*?</script>", "", RegexOptions.IgnoreCase);

            s = Regex.Replace(s, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"</p\s*>", "\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"</div\s*>", "\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<[^>]+>", " ");

            s = System.Net.WebUtility.HtmlDecode(s);

            s = Regex.Replace(
                s,
                @"(?is)il giorno\s+.+?\s+ha scritto:.*$",
                ""
            );

            s = Regex.Replace(
                s,
                @"(?is)-{2,}\s*messaggio originale\s*-{2,}.*$",
                ""
            );

            s = Regex.Replace(s, @"\s+", " ");

            return s.Trim().ToLowerInvariant();
        }
        private static string BuildLogicalEmailKey(EmailDetail_NEW x)
        {
            var sender = NormalizeEmailLikeKey(x.Mittente);
            var to = NormalizeEmailsLikeKey(x.Destinatari);
            var cc = NormalizeEmailsLikeKey(x.Cc);
            var subject = NormalizeSubjectKey(x.Oggetto);
            var date = x.Data?.ToString("yyyyMMddHHmm") ?? "";
            var body = NormalizeBodyKey(x.CorpoHtml ?? x.CorpoTesto);

            if (!string.IsNullOrWhiteSpace(sender)
                && !string.IsNullOrWhiteSpace(subject)
                && !string.IsNullOrWhiteSpace(date)
                && !string.IsNullOrWhiteSpace(body))
            {
                return $"LOGIC|{sender}|{to}|{cc}|{subject}|{date}|{body}";
            }

            var msgId = NormalizeKey(x.MessageId);
            if (!string.IsNullOrWhiteSpace(msgId))
                return "MSG|" + msgId;

            return $"{x.Tipo}-{x.Id}";
        }

        private static string NormalizeKey(string? value)
        {
            return (value ?? "")
                .Trim()
                .Trim('<', '>')
                .ToUpperInvariant();
        }

        private static string NormalizeSubjectKey(string? subject)
        {
            var s = (subject ?? "").Trim().ToLowerInvariant();

            s = Regex.Replace(s, @"^(re|r|fw|fwd)\s*:\s*", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s+", " ");

            return s;
        }
        private static string HtmlToText(string? html)
        {
            if (string.IsNullOrWhiteSpace(html))
                return "";

            var s = System.Net.WebUtility.HtmlDecode(html);
            s = FixCommonMojibake(s);

            s = Regex.Replace(s, @"<!--[\s\S]*?-->", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<style[\s\S]*?</style>", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<script[\s\S]*?</script>", "", RegexOptions.IgnoreCase);

            s = Regex.Replace(s, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"</p\s*>", "\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"</div\s*>", "\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<[^>]+>", " ");

            s = Regex.Replace(s, @"\s+", " ").Trim();

            return s;
        }

        private static string FixCommonMojibake(string? input)
        {
            var s = input ?? "";

            if (string.IsNullOrWhiteSpace(s) || !LooksLikeMojibake(s))
                return s;

            try
            {
                var bytes = System.Text.Encoding.Latin1.GetBytes(s);
                var fixedText = System.Text.Encoding.UTF8.GetString(bytes);

                if (CountMojibakeMarkers(fixedText) < CountMojibakeMarkers(s))
                    return fixedText;
            }
            catch
            {
            }

            return s;
        }

        private static bool LooksLikeMojibake(string s)
            => s.Contains("Ã") || s.Contains("Â") || s.Contains("â€") || s.Contains("â€™") || s.Contains("â€œ") || s.Contains("â€");

        private static int CountMojibakeMarkers(string s)
        {
            if (string.IsNullOrEmpty(s))
                return 0;

            return Regex.Matches(s, "Ã|Â|â€|â€™|â€œ").Count;
        }
        private static string NormalizeBodyKey(string? body)
        {
            if (string.IsNullOrWhiteSpace(body))
                return "";

            var s = HtmlToText(body)
                .ToLowerInvariant();

            s = Regex.Replace(s, @"\s+", " ").Trim();

            if (s.Length > 700)
                s = s[..700];

            return s;
        }

        private static string NormalizeEmailLikeKey(string? raw)
        {
            var email = ExtractEmailOnly(raw);
            return (email ?? "").Trim().ToLowerInvariant();
        }

        private static string NormalizeEmailsLikeKey(string? raw)
        {
            return string.Join(";",
                ParseEmailsSafe(raw)
                    .Select(x => x.Trim().ToLowerInvariant())
                    .Distinct()
                    .OrderBy(x => x));
        }
        public async Task<int> GetConversationCountByThreadFromRicevuteAsync(int emailId)
        {
            await using var conn = await GetOpenConnectionAsync();

            string threadKey;
            int? casellaId = null;

            const string findThreadSql = @"
SELECT THREAD_KEY, CASELLA_ID
FROM SGAPP.EMAIL_RICEVUTE
WHERE ID = :id";

            await using (var findCmd = new OracleCommand(findThreadSql, conn))
            {
                findCmd.BindByName = true;
                findCmd.Parameters.Add("id", OracleDbType.Int32).Value = emailId;

                await using var rd = await findCmd.ExecuteReaderAsync();
                if (await rd.ReadAsync())
                {
                    threadKey = rd.IsDBNull(0) ? "" : rd.GetString(0);
                    casellaId = rd.IsDBNull(1) ? null : rd.GetInt32(1);
                }
                else
                {
                    threadKey = "";
                }
            }

            if (string.IsNullOrWhiteSpace(threadKey) || casellaId is null)
                return 1;

            const string countSql = @"
SELECT COUNT(*)
FROM (
    SELECT LOWER(TRIM(r.MESSAGE_ID)) AS MSG_KEY
    FROM SGAPP.EMAIL_RICEVUTE r
    WHERE r.THREAD_KEY = :p_thread
            AND r.CASELLA_ID = :p_cid
      AND NVL(r.ELIMINATO, 'N') = 'N'
      AND r.MESSAGE_ID IS NOT NULL
    GROUP BY LOWER(TRIM(r.MESSAGE_ID))

    UNION

    SELECT 'NO_MSGID_' || TO_CHAR(r.ID) AS MSG_KEY
    FROM SGAPP.EMAIL_RICEVUTE r
    WHERE r.THREAD_KEY = :p_thread
        AND r.CASELLA_ID = :p_cid
      AND NVL(r.ELIMINATO, 'N') = 'N'
      AND r.MESSAGE_ID IS NULL
)";

            await using var countCmd = new OracleCommand(countSql, conn);
            countCmd.BindByName = true;
            countCmd.Parameters.Add("p_thread", OracleDbType.Varchar2).Value = threadKey;
            countCmd.Parameters.Add("p_cid", OracleDbType.Int32).Value = casellaId.Value;

            var total = await countCmd.ExecuteScalarAsync();
            return total == null ? 1 : Convert.ToInt32(total);
        }

        public async Task<List<EmailDetail_NEW>> GetConversationDetailByReceivedThreadAsync(int emailId)
        {
            await using var conn = await GetOpenConnectionAsync();

            string threadKey = "";
            int? casellaId = null;

            const string findThreadSql = @"
SELECT THREAD_KEY, CASELLA_ID
FROM SGAPP.EMAIL_RICEVUTE
WHERE ID = :id";

            await using (var findCmd = new OracleCommand(findThreadSql, conn))
            {
                findCmd.BindByName = true;
                findCmd.Parameters.Add("id", OracleDbType.Int32).Value = emailId;

                await using var rd = await findCmd.ExecuteReaderAsync();
                if (await rd.ReadAsync())
                {
                    threadKey = rd.IsDBNull(0) ? "" : rd.GetString(0);
                    casellaId = rd.IsDBNull(1) ? null : rd.GetInt32(1);
                }
            }

            if (string.IsNullOrWhiteSpace(threadKey) || casellaId is null)
                return new List<EmailDetail_NEW>();

            const string sql = @"
SELECT 
    r.ID,
    r.CASELLA_ID,
    TO_CLOB(cp.EMAIL) AS CASELLA_EMAIL,
    TO_CLOB(r.MITTENTE) AS MITTENTE,
    TO_CLOB(r.DESTINATARI) AS DESTINATARI,
    TO_CLOB(r.CC) AS CC,
    TO_CLOB(r.CCN) AS CCN,
    TO_CLOB(r.OGGETTO) AS OGGETTO,
    r.DATA_RICEZIONE AS DATA,
    TO_CLOB(r.CORPO_HTML) AS CORPO_HTML,
    TO_CLOB(r.CORPO_TESTO) AS CORPO_TESTO,
    TO_CLOB(r.MESSAGE_ID) AS MESSAGE_ID,
    TO_CLOB(r.IN_REPLY_TO) AS IN_REPLY_TO,
    TO_CLOB(r.REFERENCES_HDR) AS REFERENCES_HDR,
    TO_CLOB(r.THREAD_KEY) AS THREAD_KEY,
    'R' AS TIPO
FROM SGAPP.EMAIL_RICEVUTE r
LEFT JOIN SGAPP.CASELLEPOSTA cp ON cp.ID = r.CASELLA_ID
WHERE NVL(r.ELIMINATO, 'N') = 'N'
  AND UPPER(TRIM(r.THREAD_KEY)) = UPPER(TRIM(:p_thread))
    AND r.CASELLA_ID = :p_cid
ORDER BY r.DATA_RICEZIONE, r.ID";

            await using var cmd = new OracleCommand(sql, conn);
            cmd.BindByName = true;
            cmd.Parameters.Add("p_thread", OracleDbType.Varchar2).Value = threadKey;
            cmd.Parameters.Add("p_cid", OracleDbType.Int32).Value = casellaId.Value;

            var list = new List<EmailDetail_NEW>();

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new EmailDetail_NEW
                {
                    Id = reader.GetInt32(0),
                    CasellaId = reader.IsDBNull(1) ? null : reader.GetInt32(1),
                    CasellaEmail = GetStr(reader, 2),
                    Mittente = GetStr(reader, 3),
                    Destinatari = GetStr(reader, 4),
                    Cc = GetStr(reader, 5),
                    Ccn = GetStr(reader, 6),
                    Oggetto = GetStr(reader, 7),
                    Data = reader.IsDBNull(8) ? null : reader.GetDateTime(8),
                    CorpoHtml = GetStr(reader, 9),
                    CorpoTesto = GetStr(reader, 10),
                    MessageId = GetStr(reader, 11),
                    InReplyTo = GetStr(reader, 12),
                    References = GetStr(reader, 13),
                    ThreadKey = GetStr(reader, 14),
                    Tipo = GetStr(reader, 15),
                    IsLoaded = true
                });
            }

            const string attachSql = @"
SELECT ID, NOME_FILE, MIME_TYPE, IS_EMAIL_EML, EMAIL_EML_ID
FROM SGAPP.EMAIL_ALLEGATI
WHERE EMAIL_ID = :id_email";

            foreach (var mail in list)
            {
                mail.Allegati = new List<AllegatoItem_NEW>();

                await using var aCmd = new OracleCommand(attachSql, conn);
                aCmd.BindByName = true;
                aCmd.Parameters.Add("id_email", OracleDbType.Int32).Value = mail.Id;

                await using var aReader = await aCmd.ExecuteReaderAsync();
                while (await aReader.ReadAsync())
                {
                    mail.Allegati.Add(new AllegatoItem_NEW
                    {
                        Id = aReader.GetInt32(0),
                        NomeFile = aReader.IsDBNull(1) ? "" : aReader.GetString(1),
                        MimeType = aReader.IsDBNull(2) ? null : aReader.GetString(2),
                        IsEmailEml = !aReader.IsDBNull(3)
                            && string.Equals(aReader.GetString(3), "Y", StringComparison.OrdinalIgnoreCase),
                        EmailEmlId = aReader.IsDBNull(4) ? null : aReader.GetInt32(4)
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
    bool keepExecutorUnarchived = false,
    CancellationToken ct = default)
        {
            await using var conn = new OracleConnection(_connectionString);
            await conn.OpenAsync(ct);

            await using var tx = conn.BeginTransaction();

            try
            {
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

                foreach (var vecchioUtente in vecchiUtenti)
                {
                    var isExecutor =
                        !string.IsNullOrWhiteSpace(eseguitoDa) &&
                        vecchioUtente.Equals(eseguitoDa, StringComparison.OrdinalIgnoreCase);

                    // IO che sto assegnando
                    if (isExecutor)
                    {
                        if (keepExecutorUnarchived)
                        {
                            // "Assegna e segui"
                            await EnsureEmailSeguitaTxAsync(conn, tx, emailId, vecchioUtente, markAsRead: true, ct);
                        }
                        else
                        {
                            // "Assegna"
                            await EnsureEmailArchiviataTxAsync(conn, tx, emailId, vecchioUtente, ct);
                        }

                        continue;
                    }

                    // Tutti gli altri vecchi assegnatari -> seguiti
                    await EnsureEmailSeguitaTxAsync(conn, tx, emailId, vecchioUtente, markAsRead: false, ct);
                }

               
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

                const string sqlAssegna = @"
MERGE INTO SGAPP.EMAIL_ASSEGNAZIONI t
USING (
    SELECT :p_eid AS EMAIL_ID, :p_user AS UTENTE, :p_solo AS SOLO_INVIO
    FROM DUAL
) s
ON (
    t.EMAIL_ID = s.EMAIL_ID
    AND UPPER(t.UTENTE) = UPPER(s.UTENTE)
)
WHEN MATCHED THEN
    UPDATE SET t.SOLO_INVIO = s.SOLO_INVIO
WHEN NOT MATCHED THEN
    INSERT (EMAIL_ID, UTENTE, SOLO_INVIO)
    VALUES (s.EMAIL_ID, s.UTENTE, s.SOLO_INVIO)";

                await using (var cmd = new OracleCommand(sqlAssegna, conn))
                {
                    cmd.BindByName = true;
                    cmd.Transaction = tx;
                    cmd.Parameters.Add("p_eid", OracleDbType.Int32).Value = emailId;
                    cmd.Parameters.Add("p_user", OracleDbType.Varchar2).Value = utente;
                    cmd.Parameters.Add("p_solo", OracleDbType.Char).Value = soloInvio ? "Y" : "N";

                    await cmd.ExecuteNonQueryAsync(ct);
                }

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

                await AddAssignActivityAsync(conn, tx, emailId, eseguitoDa ?? "unknown", utente, ct);

                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }

        private static async Task EnsureEmailArchiviataTxAsync(
    OracleConnection conn,
    OracleTransaction tx,
    int emailId,
    string utente,
    CancellationToken ct = default)
        {
            const string sql = @"
INSERT INTO SGAPP.EMAIL_ARCHIVIO (ID_EMAIL, UTENTE, DATA_ARCHIVIAZIONE)
SELECT :p_eid, :p_user, SYSDATE
FROM DUAL
WHERE NOT EXISTS (
    SELECT 1
    FROM SGAPP.EMAIL_ARCHIVIO
    WHERE ID_EMAIL = :p_eid_check
      AND UPPER(UTENTE) = UPPER(:p_user_check)
)";

            await using var cmd = new OracleCommand(sql, conn)
            {
                BindByName = true,
                Transaction = tx
            };

            cmd.Parameters.Add("p_eid", OracleDbType.Int32).Value = emailId;
            cmd.Parameters.Add("p_user", OracleDbType.Varchar2).Value = utente;
            cmd.Parameters.Add("p_eid_check", OracleDbType.Int32).Value = emailId;
            cmd.Parameters.Add("p_user_check", OracleDbType.Varchar2).Value = utente;

            await cmd.ExecuteNonQueryAsync(ct);
        }

        private static async Task EnsureEmailSeguitaTxAsync(
    OracleConnection conn,
    OracleTransaction tx,
    int emailId,
    string utente,
    bool markAsRead,
    CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(utente))
                return;

            var utenteNorm = utente.Trim().ToLowerInvariant();

            const string sqlUnarchive = @"
DELETE FROM SGAPP.EMAIL_ARCHIVIO
WHERE ID_EMAIL = :p_eid
  AND UPPER(UTENTE) = UPPER(:p_user)";

            await using (var unarchiveCmd = new OracleCommand(sqlUnarchive, conn)
            {
                BindByName = true,
                Transaction = tx
            })
            {
                unarchiveCmd.Parameters.Add("p_eid", OracleDbType.Int32).Value = emailId;
                unarchiveCmd.Parameters.Add("p_user", OracleDbType.Varchar2).Value = utenteNorm;

                await unarchiveCmd.ExecuteNonQueryAsync(ct);
            }

            var letto = markAsRead ? "Y" : "N";

            const string sql = @"
MERGE INTO SGAPP.EMAIL_SEGUITE t
USING (
    SELECT :p_eid AS EMAIL_ID, :p_user AS UTENTE
    FROM DUAL
) s
ON (
    t.EMAIL_ID = s.EMAIL_ID
    AND UPPER(t.UTENTE) = UPPER(s.UTENTE)
)
WHEN MATCHED THEN
    UPDATE SET 
        t.LETTO = :p_letto,
        t.LETTO_IL = CASE WHEN :p_letto = 'Y' THEN SYSDATE ELSE NULL END
WHEN NOT MATCHED THEN
    INSERT (EMAIL_ID, UTENTE, CREATA_IL, LETTO, LETTO_IL)
    VALUES (
        s.EMAIL_ID,
        s.UTENTE,
        SYSDATE,
        :p_letto,
        CASE WHEN :p_letto = 'Y' THEN SYSDATE ELSE NULL END
    )";

            await using var cmd = new OracleCommand(sql, conn)
            {
                BindByName = true,
                Transaction = tx
            };

            cmd.Parameters.Add("p_eid", OracleDbType.Int32).Value = emailId;
            cmd.Parameters.Add("p_user", OracleDbType.Varchar2).Value = utenteNorm;
            cmd.Parameters.Add("p_letto", OracleDbType.Char).Value = letto;

            await cmd.ExecuteNonQueryAsync(ct);
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

        public async Task<List<EmailListItem_NEW>> SearchEmailsAdvancedFastAsync(
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

            var w = (word ?? "").Trim().ToLowerInvariant();
            var a = (address ?? "").Trim().ToLowerInvariant();

            bool hasWord = !string.IsNullOrWhiteSpace(w) && w.Length >= 2;
            bool hasAddr = !string.IsNullOrWhiteSpace(a) && a.Length >= 2;
            bool includeBodySearch = hasWord && w.Length >= 4 && !w.Contains('@');

            if (!hasWord && !hasAddr)
                return new List<EmailListItem_NEW>();

            casellaIds = casellaIds?.Distinct().ToList() ?? new List<int>();

            if (folderUi == "all" && casellaIds.Count == 0)
                return new List<EmailListItem_NEW>();

            var whereSql = @"WHERE 1=1";
            var minDate = folderUi == "all"
                ? null
                : GetMailUiMinDate();

            if (minDate.HasValue)
            {
                whereSql += @"
AND e.DATA_RICEZIONE >= :minDate";
            }

            if (folderUi == "inbox")
            {
                whereSql += @"
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
                whereSql += @"
AND EXISTS (
    SELECT 1
    FROM SGAPP.EMAIL_ARCHIVIO a
    WHERE a.ID_EMAIL = e.ID
      AND a.UTENTE = :utente
)";
            }
            else if (folderUi == "all")
            {
                whereSql += " AND e.CASELLA_ID IN (" + string.Join(",", casellaIds) + ")";
            }

            if (hasAddr)
            {
                whereSql += @"
AND (
      LOWER(NVL(e.MITTENTE,''))    LIKE :addr
   OR LOWER(NVL(e.DESTINATARI,'')) LIKE :addr
   OR LOWER(NVL(e.CC,''))          LIKE :addr
   OR LOWER(NVL(e.CCN,''))         LIKE :addr
)";
            }

            if (hasWord)
            {
                                whereSql += @"
AND (
            LOWER(NVL(e.OGGETTO,''))      LIKE :word
     OR LOWER(NVL(e.MITTENTE,''))     LIKE :word
     OR LOWER(NVL(e.DESTINATARI,''))  LIKE :word
     OR LOWER(NVL(e.CC,''))           LIKE :word
     OR LOWER(NVL(e.CCN,''))          LIKE :word";

                                if (includeBodySearch)
                                {
                                        whereSql += @"
     OR LOWER(DBMS_LOB.SUBSTR(NVL(e.CORPO_TESTO, e.CORPO_HTML), 1200, 1)) LIKE :word";
                                }

                                whereSql += @"
     OR EXISTS (
                SELECT 1
                FROM SGAPP.EMAIL_ALLEGATI a
                WHERE a.EMAIL_ID = e.ID
                    AND LOWER(NVL(a.NOME_FILE,'')) LIKE :word
     )
)";
            }

            var sql = $@"
WITH base_rows AS (
    SELECT
        e.ID,
        e.THREAD_KEY,
        e.MITTENTE,
        e.DESTINATARI,
        e.OGGETTO,
        e.DATA_RICEZIONE,
        e.CASELLA_ID,
        e.FOLDER_PATH,
        c.EMAIL AS CASELLA_EMAIL,
        e.APERTO,
        CASE
            WHEN EXISTS (
                SELECT 1
                FROM SGAPP.EMAIL_LETTURE_UTENTE l
                WHERE l.EMAIL_ID = e.ID
                  AND LOWER(NVL(l.UTENTE, '')) = LOWER(NVL(:utente, ''))
            )
            THEN 1
            ELSE 0
        END AS IS_READ_BY_ME,
        e.CC,
        e.CCN,
        SUBSTR(NVL(e.CORPO_TESTO, e.CORPO_HTML), 1, 200) AS PREVIEW,
        (
            SELECT LISTAGG(x.UTENTE, ';') WITHIN GROUP (ORDER BY x.ID)
            FROM SGAPP.EMAIL_ASSEGNAZIONI x
            WHERE x.EMAIL_ID = e.ID
        ) AS ASSEGNATO_A,
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
        ROW_NUMBER() OVER (
            PARTITION BY NVL(e.THREAD_KEY, 'SINGLE_' || e.ID)
            ORDER BY e.DATA_RICEZIONE DESC, e.ID DESC
        ) AS RN,
        MAX(
            CASE
                WHEN EXISTS (
                    SELECT 1
                    FROM SGAPP.EMAIL_LETTURE_UTENTE l
                    WHERE l.EMAIL_ID = e.ID
                      AND LOWER(NVL(l.UTENTE, '')) = LOWER(NVL(:utente, ''))
                )
                THEN 0
                ELSE 1
            END
        ) OVER (
            PARTITION BY NVL(e.THREAD_KEY, 'SINGLE_' || e.ID)
        ) AS HAS_UNREAD,
        COUNT(*) OVER (
            PARTITION BY NVL(e.THREAD_KEY, 'SINGLE_' || e.ID)
        ) AS THREAD_LEN
    FROM SGAPP.EMAIL_RICEVUTE e
    JOIN SGAPP.CASELLEPOSTA c ON c.ID = e.CASELLA_ID
    {whereSql}
)
SELECT
    ID,
    THREAD_KEY,
    MITTENTE,
    DESTINATARI,
    OGGETTO,
    DATA_RICEZIONE,
    CASELLA_ID,
    FOLDER_PATH,
    CASELLA_EMAIL,
    CASE WHEN HAS_UNREAD = 1 THEN 'N' ELSE 'Y' END AS APERTO,
    IS_READ_BY_ME,
    CC,
    CCN,
    HAS_ATTACH,
    ATT_PACK,
    PREVIEW,
    ASSEGNATO_A,
    THREAD_LEN
FROM base_rows
WHERE RN = 1
ORDER BY DATA_RICEZIONE DESC, ID DESC
OFFSET :p_start ROWS FETCH NEXT :p_pageSizePlusOne ROWS ONLY";

            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };

            if (minDate.HasValue)
                cmd.Parameters.Add("minDate", OracleDbType.Date).Value = minDate.Value.Date;

            cmd.Parameters.Add("utente", OracleDbType.Varchar2).Value = utente ?? string.Empty;

            if (hasAddr)
                cmd.Parameters.Add("addr", OracleDbType.Varchar2).Value = $"%{a}%";

            if (hasWord)
                cmd.Parameters.Add("word", OracleDbType.Varchar2).Value = $"%{w}%";

            cmd.Parameters.Add("p_start", OracleDbType.Int32).Value = start;
            cmd.Parameters.Add("p_pageSizePlusOne", OracleDbType.Int32).Value = pageSize + 1;

            var rawRows = new List<(
    int Id,
    DateTime? Data,
    string Mittente,
    string Oggetto,
    string Aperto,
    bool HasAttachments,
    string? Preview,
    List<AllegatoItem_NEW>? Allegati,
    int CasellaId,
    string? CasellaEmail,
    string? AssegnatoA,
    string? Destinatari,
    string? Cc,
    string? Ccn,
    string? ThreadKey,
    bool IsReadByCurrentUser
)>();

            await using var reader = await cmd.ExecuteReaderAsync();
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

                rawRows.Add((
                    Id: reader.GetInt32("ID"),
                    Data: reader.IsDBNull("DATA_RICEZIONE") ? (DateTime?)null : reader.GetDateTime("DATA_RICEZIONE"),
                    Mittente: reader.IsDBNull("MITTENTE") ? "" : reader.GetString("MITTENTE"),
                    Oggetto: reader.IsDBNull("OGGETTO") ? "" : reader.GetString("OGGETTO"),
                    Aperto: reader.IsDBNull("APERTO") ? "" : reader.GetString("APERTO"),
                    HasAttachments: reader.GetInt32("HAS_ATTACH") == 1,
                    Preview: reader.IsDBNull("PREVIEW") ? null : reader.GetString("PREVIEW"),
                    Allegati: allegati,
                    CasellaId: reader.GetInt32("CASELLA_ID"),
                    CasellaEmail: reader.IsDBNull("CASELLA_EMAIL") ? null : reader.GetString("CASELLA_EMAIL"),
                    AssegnatoA: reader.IsDBNull("ASSEGNATO_A") ? null : reader.GetString("ASSEGNATO_A"),
                    Destinatari: GetStr(reader, "DESTINATARI"),
                    Cc: GetStr(reader, "CC"),
                    Ccn: GetStr(reader, "CCN"),
                    ThreadKey: reader.IsDBNull("THREAD_KEY") ? null : reader.GetString("THREAD_KEY"),
                    IsReadByCurrentUser: !reader.IsDBNull("IS_READ_BY_ME") && reader.GetInt32("IS_READ_BY_ME") == 1
                ));
            }

            var threadCounts = await GetRealThreadCountsAsync(conn, rawRows.Select(x => x.Id).ToList());

            var list = rawRows.Select(r =>
            {
                var threadLen = threadCounts.TryGetValue(r.Id, out var len) ? len : 1;

                return new EmailListItem_NEW(
                    Id: r.Id,
                    Data: r.Data,
                    Mittente: r.Mittente,
                    Oggetto: r.Oggetto,
                    Aperto: r.Aperto,
                    HasAttachments: r.HasAttachments,
                    ThreadLen: threadLen,
                    Replies: Math.Max(0, threadLen - 1),
                    Preview: r.Preview,
                    Allegati: r.Allegati,
                    MessageId: null,
                    CasellaId: r.CasellaId,
                    CasellaEmail: r.CasellaEmail,
                    AssegnatoA: r.AssegnatoA,
                    Destinatari: r.Destinatari,
                    Cc: r.Cc,
                    Ccn: r.Ccn,
                    ThreadKey: r.ThreadKey,
                    IsReadByCurrentUser: r.IsReadByCurrentUser
                );
            }).ToList();

            return list;
        }

        private async Task<Dictionary<int, int>> GetRealThreadCountsAsync(
    OracleConnection conn,
    List<int> emailIds)
{
    var result = new Dictionary<int, int>();

    if (emailIds == null || emailIds.Count == 0)
        return result;

    var ids = emailIds.Distinct().ToList();
    var inList = string.Join(",", ids);

    var sql = $@"
WITH seed AS (
    SELECT
        r.ID,
        r.THREAD_KEY
    FROM SGAPP.EMAIL_RICEVUTE r
    WHERE r.ID IN ({inList})
),
thread_totals AS (
    SELECT
        x.THREAD_KEY,
        COUNT(*) AS TOT
    FROM (
        SELECT r.THREAD_KEY
        FROM SGAPP.EMAIL_RICEVUTE r
        WHERE r.THREAD_KEY IN (SELECT DISTINCT THREAD_KEY FROM seed WHERE THREAD_KEY IS NOT NULL)
          AND NVL(r.ELIMINATO, 'N') = 'N'

        UNION ALL

        SELECT i.THREAD_KEY
        FROM SGAPP.EMAIL_INVIATE i
        WHERE i.THREAD_KEY IN (SELECT DISTINCT THREAD_KEY FROM seed WHERE THREAD_KEY IS NOT NULL)
    ) x
    GROUP BY x.THREAD_KEY
)
SELECT
    s.ID,
    CASE
        WHEN s.THREAD_KEY IS NULL THEN 1
        ELSE NVL(t.TOT, 1)
    END AS THREAD_LEN
FROM seed s
LEFT JOIN thread_totals t
    ON t.THREAD_KEY = s.THREAD_KEY";

    await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
    await using var reader = await cmd.ExecuteReaderAsync();

    while (await reader.ReadAsync())
    {
        var id = reader.GetInt32(0);
        var len = reader.IsDBNull(1) ? 1 : reader.GetInt32(1);
        result[id] = len;
    }

    return result;
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

        public async Task<string?> GetAssegnatariEmailAsync(int emailId)
        {
            await using var db = _dbFactory.CreateDbContext();

            var utenti = await db.EmailAssegnazione
                .AsNoTracking()
                .Where(a => a.EmailId == emailId)
                .Select(a => a.Utente)
                .Where(u => u != null && u != "")
                .Distinct()
                .OrderBy(u => u)
                .ToListAsync();

            return utenti.Count == 0
                ? null
                : string.Join("; ", utenti);
        }

        public async Task<Dictionary<int, int>> GetConversationCountsByThreadAsync(List<int> emailIds)
        {
            var result = new Dictionary<int, int>();

            if (emailIds == null || emailIds.Count == 0)
                return result;

            var ids = emailIds.Distinct().ToList();
            var inList = string.Join(",", ids);

            await using var conn = new OracleConnection(_connectionString);
            await conn.OpenAsync();

            var sql = $@"
WITH selected_mails AS (
    SELECT
        r.ID,
        r.THREAD_KEY
    FROM SGAPP.EMAIL_RICEVUTE r
    WHERE r.ID IN ({inList})
),
thread_counts AS (
    SELECT
        t.THREAD_KEY,
        COUNT(*) AS CNT
    FROM (
        SELECT r.THREAD_KEY
        FROM SGAPP.EMAIL_RICEVUTE r
        WHERE r.THREAD_KEY IN (
            SELECT DISTINCT THREAD_KEY
            FROM selected_mails
            WHERE THREAD_KEY IS NOT NULL
        )
          AND NVL(r.ELIMINATO, 'N') = 'N'

        UNION ALL

        SELECT i.THREAD_KEY
        FROM SGAPP.EMAIL_INVIATE i
        WHERE i.THREAD_KEY IN (
            SELECT DISTINCT THREAD_KEY
            FROM selected_mails
            WHERE THREAD_KEY IS NOT NULL
        )
    ) t
    GROUP BY t.THREAD_KEY
)
SELECT
    s.ID,
    CASE
        WHEN s.THREAD_KEY IS NULL THEN 1
        ELSE NVL(tc.CNT, 1)
    END AS THREAD_LEN
FROM selected_mails s
LEFT JOIN thread_counts tc
    ON tc.THREAD_KEY = s.THREAD_KEY";

            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                var id = reader.GetInt32(0);
                var len = reader.IsDBNull(1) ? 1 : reader.GetInt32(1);
                result[id] = len;
            }

            return result;
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

        public async Task<(List<EmailListItem_NEW> page, int total)> GetFollowedEmailsPagedAsync(
    string utente,
    int start,
    int pageSize,
    string? word = null,
    string? address = null)
{
    if (string.IsNullOrWhiteSpace(utente))
        return (new List<EmailListItem_NEW>(), 0);

    await using var db = _dbFactory.CreateDbContext();

    var utenteNorm = utente.Trim().ToLowerInvariant();
    var wordNorm = word?.Trim().ToLowerInvariant();
    var addrNorm = address?.Trim().ToLowerInvariant();

    var query =
        from e in db.EmailRicevute
        join s in db.EmailSeguite on e.Id equals s.EmailId
        join c in db.CasellePosta on e.CasellaId equals c.Id into caselle
        from casella in caselle.DefaultIfEmpty()
        where s.Utente == utenteNorm
              && !db.EmailArchivio.Any(a => a.IdEmail == e.Id && a.Utente == utenteNorm)
        select new { e, s, casella };

    if (!string.IsNullOrWhiteSpace(wordNorm) && wordNorm.Length >= 2)
    {
        query = query.Where(x =>
            (x.e.Oggetto != null && x.e.Oggetto.ToLower().Contains(wordNorm)) ||
            (x.e.Mittente != null && x.e.Mittente.ToLower().Contains(wordNorm)) ||
            (x.e.CorpoTesto != null && x.e.CorpoTesto.ToLower().Contains(wordNorm)));
    }

    if (!string.IsNullOrWhiteSpace(addrNorm))
    {
        query = query.Where(x =>
            (x.e.Mittente != null && x.e.Mittente.ToLower().Contains(addrNorm)) ||
            (x.e.Destinatari != null && x.e.Destinatari.ToLower().Contains(addrNorm)) ||
            (x.casella != null && x.casella.Email != null && x.casella.Email.ToLower().Contains(addrNorm)));
    }

    var total = await query.CountAsync();

    var rawPage = await query
        .AsNoTracking()
        .OrderBy(x => x.s.Letto == "Y" ? 1 : 0)
        .ThenByDescending(x => x.e.DataRicezione)
        .Skip(start)
        .Take(pageSize)
        .Select(x => new
        {
            Id = x.e.Id,
            Data = x.e.DataRicezione,
            Mittente = x.e.Mittente,
            Oggetto = x.e.Oggetto,
            Aperto = x.e.Aperto,
            Preview = x.e.CorpoTesto != null
                ? x.e.CorpoTesto.Substring(0, Math.Min(200, x.e.CorpoTesto.Length))
                : null,
            MessageId = x.e.MessageId,
            CasellaId = x.e.CasellaId,
            CasellaEmail = x.casella != null ? x.casella.Email : null,
            ThreadKey = x.e.ThreadKey,
            Destinatari = x.e.Destinatari,
            LettoSeguita = x.s.Letto,
            LettoSeguitaIl = x.s.LettoIl
        })
        .ToListAsync();
            var idsRaw = rawPage.Select(x => x.Id).Distinct().ToList();

            var assegnazioni = await db.EmailAssegnazione
                .AsNoTracking()
                .Where(a => idsRaw.Contains(a.EmailId)
                         && (a.SoloInvio == null || a.SoloInvio != "Y"))
                .Select(a => new
                {
                    a.EmailId,
                    a.Utente
                })
                .ToListAsync();

            var assegnatiMap = assegnazioni
                .GroupBy(a => a.EmailId)
                .ToDictionary(
                    g => g.Key,
                    g => string.Join("; ", g
                        .Select(x => x.Utente)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct()
                        .OrderBy(x => x))
                );
            var page = rawPage
        .Select(x => new EmailListItem_NEW(
            Id: x.Id,
            Data: x.Data,
            Mittente: x.Mittente,
            Oggetto: x.Oggetto,
            Aperto: x.Aperto,
            HasAttachments: false,
            ThreadLen: 0,
            Replies: 0,
            Preview: x.Preview,
            Allegati: null,
            MessageId: x.MessageId,
            CasellaId: x.CasellaId,
            CasellaEmail: x.CasellaEmail,
            ThreadKey: x.ThreadKey,
            AssegnatoA: assegnatiMap.TryGetValue(x.Id, out var ass) ? ass : "",
            Destinatari: x.Destinatari,
            Cc: null,
            Ccn: null,
            LettoSeguita: x.LettoSeguita,
            LettoSeguitaIl: x.LettoSeguitaIl,
            CanArchive: true,
            IsReadByCurrentUser: true
        ))
        .ToList();

    var ids = page.Select(x => x.Id).ToList();

    if (ids.Any())
    {
        var allegati = await db.EmailAllegati
            .AsNoTracking()
            .Where(a => ids.Contains(a.EmailId))
            .Select(a => new
            {
                a.Id,
                a.EmailId,
                a.NomeFile,
                a.MimeType
            })
            .ToListAsync();

        page = page
            .Select(mail =>
            {
                var allegatiMail = allegati
                    .Where(a => a.EmailId == mail.Id)
                    .Select(a => new AllegatoItem_NEW(
                        a.Id,
                        a.NomeFile,
                        a.MimeType
                    ))
                    .ToList();

                return new EmailListItem_NEW(
                    Id: mail.Id,
                    Data: mail.Data,
                    Mittente: mail.Mittente,
                    Oggetto: mail.Oggetto,
                    Aperto: mail.Aperto,
                    HasAttachments: allegatiMail.Any(),
                    ThreadLen: mail.ThreadLen,
                    Replies: mail.Replies,
                    Preview: mail.Preview,
                    Allegati: allegatiMail,
                    MessageId: mail.MessageId,
                    CasellaId: mail.CasellaId,
                    CasellaEmail: mail.CasellaEmail,
                    ThreadKey: mail.ThreadKey,
                    AssegnatoA: mail.AssegnatoA,
                    Destinatari: mail.Destinatari,
                    Cc: mail.Cc,
                    Ccn: mail.Ccn,
                    LettoSeguita: mail.LettoSeguita,
                    LettoSeguitaIl: mail.LettoSeguitaIl,
                    CanArchive: mail.CanArchive,
                    IsReadByCurrentUser: mail.IsReadByCurrentUser
                );
            })
            .ToList();
    }

    return (page, total);
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

            casellaIds = casellaIds?.Distinct().ToList() ?? new List<int>();

            if (folderUi == "all" && casellaIds.Count == 0)
                return (new List<EmailListItem_NEW>(), 0);

            var whereSql = @"
WHERE 1=1";

            var minDate = folderUi == "all"
                ? null
                : GetMailUiMinDate();

            if (minDate.HasValue)
            {
                whereSql += @"
      AND e.DATA_RICEZIONE >= :minDate";
            }

            if (folderUi != "blacklist")
            {
                whereSql += @"
      AND NVL(e.BLACKLIST, 'N') <> 'Y'";
            }
            else
            {
                whereSql += @"
      AND NVL(e.BLACKLIST, 'N') = 'Y'";
            }

            if (folderUi == "inbox")
            {
                whereSql += @"
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
                whereSql += @"
    AND EXISTS (
        SELECT 1
        FROM SGAPP.EMAIL_ARCHIVIO a
        WHERE a.ID_EMAIL = e.ID
          AND a.UTENTE = :utente
    )";
            }
            else if (folderUi == "all")
            {
                whereSql += " AND e.CASELLA_ID IN (" + string.Join(",", casellaIds) + ")";
            }

            if (!string.IsNullOrWhiteSpace(filtro))
            {
                whereSql += @"
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

            // =========================================================
            // RAMO PIÙ LEGGERO PER "ALL", MA CON ALLEGATI
            // =========================================================
            if (folderUi == "all")
            {
                string listSqlAll = $@"
SELECT
    e.ID,
    e.THREAD_KEY,
    e.MITTENTE,
    e.DESTINATARI,
    e.OGGETTO,
    e.DATA_RICEZIONE,
    e.CASELLA_ID,
    e.FOLDER_PATH,
    c.EMAIL AS CASELLA_EMAIL,
    NVL(e.APERTO, 'N') AS APERTO,

    CASE
        WHEN EXISTS (
            SELECT 1
            FROM SGAPP.EMAIL_LETTURE_UTENTE l
            WHERE l.EMAIL_ID = e.ID
              AND l.UTENTE IS NOT NULL
              AND INSTR(
                    ';' || LOWER(REPLACE(l.UTENTE, ' ', '')) || ';',
                    ';' || LOWER(REPLACE(:utente, ' ', '')) || ';'
                  ) > 0
        )
        THEN 1
        ELSE 0
    END AS IS_READ_BY_ME,

    e.CC,
    e.CCN,
    SUBSTR(NVL(e.CORPO_TESTO, e.CORPO_HTML), 1, 200) AS PREVIEW,

    CASE WHEN EXISTS (
        SELECT 1
        FROM SGAPP.EMAIL_ALLEGATI a
        WHERE a.EMAIL_ID = e.ID
    ) THEN 1 ELSE 0 END AS HAS_ATTACH,

    (
        SELECT LISTAGG(
            a.ID || '::' || a.NOME_FILE || '::' || NVL(a.MIME_TYPE,''),
            '||'
        ) WITHIN GROUP (ORDER BY a.ID)
        FROM SGAPP.EMAIL_ALLEGATI a
        WHERE a.EMAIL_ID = e.ID
    ) AS ATT_PACK

FROM SGAPP.EMAIL_RICEVUTE e
JOIN SGAPP.CASELLEPOSTA c ON c.ID = e.CASELLA_ID
{whereSql}
ORDER BY e.DATA_RICEZIONE DESC, e.ID DESC
OFFSET :p_start ROWS FETCH NEXT :p_pageSize ROWS ONLY";

                await using var cmdAll = new OracleCommand(listSqlAll, conn) { BindByName = true };

                cmdAll.Parameters.Add("utente", OracleDbType.Varchar2).Value = utente;

                if (minDate.HasValue)
                    cmdAll.Parameters.Add("minDate", OracleDbType.Date).Value = minDate.Value.Date;

                if (!string.IsNullOrWhiteSpace(filtro))
                    cmdAll.Parameters.Add("filtro", OracleDbType.Varchar2).Value = $"%{filtro.Trim().ToLower()}%";

                cmdAll.Parameters.Add("p_start", OracleDbType.Int32).Value = start;
                cmdAll.Parameters.Add("p_pageSize", OracleDbType.Int32).Value = pageSize;

                var listAll = new List<EmailListItem_NEW>();

                await using (var reader = await cmdAll.ExecuteReaderAsync())
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

                        listAll.Add(new EmailListItem_NEW(
                            Id: reader.GetInt32("ID"),
                            Data: reader.IsDBNull("DATA_RICEZIONE") ? (DateTime?)null : reader.GetDateTime("DATA_RICEZIONE"),
                            Mittente: reader.IsDBNull("MITTENTE") ? "" : reader.GetString("MITTENTE"),
                            Oggetto: reader.IsDBNull("OGGETTO") ? "" : reader.GetString("OGGETTO"),
                            Aperto: reader.IsDBNull("APERTO") ? "" : reader.GetString("APERTO"),
                            HasAttachments: !reader.IsDBNull("HAS_ATTACH") && reader.GetInt32("HAS_ATTACH") == 1,
                            ThreadLen: 1,
                            Replies: 0,
                            Preview: reader.IsDBNull("PREVIEW") ? null : reader.GetString("PREVIEW"),
                            Allegati: allegati,
                            MessageId: null,
                            CasellaId: reader.GetInt32("CASELLA_ID"),
                            CasellaEmail: reader.IsDBNull("CASELLA_EMAIL") ? null : reader.GetString("CASELLA_EMAIL"),
                            AssegnatoA: null,
                            Destinatari: GetStr(reader, "DESTINATARI"),
                            Cc: GetStr(reader, "CC"),
                            Ccn: GetStr(reader, "CCN"),
                            ThreadKey: reader.IsDBNull("THREAD_KEY") ? null : reader.GetString("THREAD_KEY"),
                            IsReadByCurrentUser: !reader.IsDBNull("IS_READ_BY_ME") && reader.GetInt32("IS_READ_BY_ME") == 1
                        ));
                    }
                }

                int totalAll = start + listAll.Count + (listAll.Count == pageSize ? 1 : 0);
                return (listAll, totalAll);
            }

            // =========================================================
            // RAMO COMPLETO PER INBOX / MYARCHIVE
            // =========================================================
            string listSql = $@"
WITH base_rows AS (
    SELECT
        e.ID,
        e.THREAD_KEY,
        e.MITTENTE,
        e.DESTINATARI,
        e.OGGETTO,
        e.DATA_RICEZIONE,
        e.CASELLA_ID,
        e.FOLDER_PATH,
        c.EMAIL AS CASELLA_EMAIL,
        e.APERTO,
        e.CC,
        e.CCN,
        SUBSTR(NVL(e.CORPO_TESTO, e.CORPO_HTML), 1, 200) AS PREVIEW,

        (
            SELECT LISTAGG(x.UTENTE, ';') WITHIN GROUP (ORDER BY x.ID)
            FROM SGAPP.EMAIL_ASSEGNAZIONI x
            WHERE x.EMAIL_ID = e.ID
        ) AS ASSEGNATO_A,

        CASE WHEN EXISTS (
            SELECT 1
            FROM SGAPP.EMAIL_ALLEGATI a
            WHERE a.EMAIL_ID = e.ID
        ) THEN 1 ELSE 0 END AS HAS_ATTACH,

        (
            SELECT LISTAGG(
                a.ID || '::' || a.NOME_FILE || '::' || NVL(a.MIME_TYPE,''),
                '||'
            ) WITHIN GROUP (ORDER BY a.ID)
            FROM SGAPP.EMAIL_ALLEGATI a
            WHERE a.EMAIL_ID = e.ID
        ) AS ATT_PACK,

        CASE
            WHEN EXISTS (
                SELECT 1
                FROM SGAPP.EMAIL_LETTURE_UTENTE l
                WHERE l.EMAIL_ID = e.ID
                  AND l.UTENTE IS NOT NULL
                  AND INSTR(
                        ';' || LOWER(REPLACE(l.UTENTE, ' ', '')) || ';',
                        ';' || LOWER(REPLACE(:utente, ' ', '')) || ';'
                      ) > 0
            )
            THEN 1
            ELSE 0
        END AS IS_READ_BY_ME,

        ROW_NUMBER() OVER (
            PARTITION BY NVL(e.THREAD_KEY, 'SINGLE_' || e.ID)
            ORDER BY e.DATA_RICEZIONE DESC, e.ID DESC
        ) AS RN,

        MAX(
            CASE
                WHEN EXISTS (
                    SELECT 1
                    FROM SGAPP.EMAIL_LETTURE_UTENTE l
                    WHERE l.EMAIL_ID = e.ID
                      AND l.UTENTE IS NOT NULL
                      AND INSTR(
                            ';' || LOWER(REPLACE(l.UTENTE, ' ', '')) || ';',
                            ';' || LOWER(REPLACE(:utente, ' ', '')) || ';'
                          ) > 0
                )
                THEN 0
                ELSE 1
            END
        ) OVER (
            PARTITION BY NVL(e.THREAD_KEY, 'SINGLE_' || e.ID)
        ) AS HAS_UNREAD,

        COUNT(*) OVER (
            PARTITION BY NVL(e.THREAD_KEY, 'SINGLE_' || e.ID)
        ) AS THREAD_LEN

    FROM SGAPP.EMAIL_RICEVUTE e
    JOIN SGAPP.CASELLEPOSTA c ON c.ID = e.CASELLA_ID
    {whereSql}
)
SELECT
    ID,
    THREAD_KEY,
    MITTENTE,
    DESTINATARI,
    OGGETTO,
    DATA_RICEZIONE,
    CASELLA_ID,
    FOLDER_PATH,
    CASELLA_EMAIL,
    CASE WHEN HAS_UNREAD = 1 THEN 'N' ELSE 'Y' END AS APERTO,
    IS_READ_BY_ME,
    CC,
    CCN,
    HAS_ATTACH,
    ATT_PACK,
    PREVIEW,
    ASSEGNATO_A,
    THREAD_LEN
FROM base_rows
WHERE RN = 1
ORDER BY DATA_RICEZIONE DESC, ID DESC
OFFSET :p_start ROWS FETCH NEXT :p_pageSize ROWS ONLY";

            await using var cmd = new OracleCommand(listSql, conn) { BindByName = true };

            cmd.Parameters.Add("utente", OracleDbType.Varchar2).Value = utente;

            if (minDate.HasValue)
                cmd.Parameters.Add("minDate", OracleDbType.Date).Value = minDate.Value.Date;

            if (!string.IsNullOrWhiteSpace(filtro))
                cmd.Parameters.Add("filtro", OracleDbType.Varchar2).Value = $"%{filtro.Trim().ToLower()}%";

            cmd.Parameters.Add("p_start", OracleDbType.Int32).Value = start;
            cmd.Parameters.Add("p_pageSize", OracleDbType.Int32).Value = pageSize;

            var rawRows = new List<(
                int Id,
                DateTime? Data,
                string Mittente,
                string Oggetto,
                string Aperto,
                bool HasAttachments,
                string? Preview,
                List<AllegatoItem_NEW>? Allegati,
                int CasellaId,
                string? CasellaEmail,
                string? AssegnatoA,
                string? Destinatari,
                string? Cc,
                string? Ccn,
                string? ThreadKey,
                bool IsReadByCurrentUser
            )>();

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

                    rawRows.Add((
                        Id: reader.GetInt32("ID"),
                        Data: reader.IsDBNull("DATA_RICEZIONE") ? (DateTime?)null : reader.GetDateTime("DATA_RICEZIONE"),
                        Mittente: reader.IsDBNull("MITTENTE") ? "" : reader.GetString("MITTENTE"),
                        Oggetto: reader.IsDBNull("OGGETTO") ? "" : reader.GetString("OGGETTO"),
                        Aperto: reader.IsDBNull("APERTO") ? "" : reader.GetString("APERTO"),
                        HasAttachments: reader.GetInt32("HAS_ATTACH") == 1,
                        Preview: reader.IsDBNull("PREVIEW") ? null : reader.GetString("PREVIEW"),
                        Allegati: allegati,
                        CasellaId: reader.GetInt32("CASELLA_ID"),
                        CasellaEmail: reader.IsDBNull("CASELLA_EMAIL") ? null : reader.GetString("CASELLA_EMAIL"),
                        AssegnatoA: reader.IsDBNull("ASSEGNATO_A") ? null : reader.GetString("ASSEGNATO_A"),
                        Destinatari: GetStr(reader, "DESTINATARI"),
                        Cc: GetStr(reader, "CC"),
                        Ccn: GetStr(reader, "CCN"),
                        ThreadKey: reader.IsDBNull("THREAD_KEY") ? null : reader.GetString("THREAD_KEY"),
                        IsReadByCurrentUser: !reader.IsDBNull("IS_READ_BY_ME") && reader.GetInt32("IS_READ_BY_ME") == 1
                    ));
                }
            }

            var threadCounts = await GetRealThreadCountsAsync(conn, rawRows.Select(x => x.Id).ToList());

            var list = rawRows.Select(r =>
            {
                var threadLen = threadCounts.TryGetValue(r.Id, out var len) ? len : 1;

                return new EmailListItem_NEW(
                    Id: r.Id,
                    Data: r.Data,
                    Mittente: r.Mittente,
                    Oggetto: r.Oggetto,
                    Aperto: r.Aperto,
                    HasAttachments: r.HasAttachments,
                    ThreadLen: threadLen,
                    Replies: Math.Max(0, threadLen - 1),
                    Preview: r.Preview,
                    Allegati: r.Allegati,
                    MessageId: null,
                    CasellaId: r.CasellaId,
                    CasellaEmail: r.CasellaEmail,
                    AssegnatoA: r.AssegnatoA,
                    Destinatari: r.Destinatari,
                    Cc: r.Cc,
                    Ccn: r.Ccn,
                    ThreadKey: r.ThreadKey,
                    IsReadByCurrentUser: r.IsReadByCurrentUser
                );
            }).ToList();

            string countSql = $@"
SELECT COUNT(*)
FROM (
    SELECT
        ROW_NUMBER() OVER (
            PARTITION BY NVL(e.THREAD_KEY, 'SINGLE_' || e.ID)
            ORDER BY e.DATA_RICEZIONE DESC, e.ID DESC
        ) AS RN
    FROM SGAPP.EMAIL_RICEVUTE e
    {whereSql}
)
WHERE RN = 1";

            await using var countCmd = new OracleCommand(countSql, conn) { BindByName = true };

            if (minDate.HasValue)
                countCmd.Parameters.Add("minDate", OracleDbType.Date).Value = minDate.Value.Date;

            if (folderUi == "inbox" || folderUi == "myarchive")
                countCmd.Parameters.Add("utente", OracleDbType.Varchar2).Value = utente;

            if (!string.IsNullOrWhiteSpace(filtro))
                countCmd.Parameters.Add("filtro", OracleDbType.Varchar2).Value = $"%{filtro.Trim().ToLower()}%";

            int total = Convert.ToInt32(await countCmd.ExecuteScalarAsync());

            return (list, total);
        }

        static string? GetStr(OracleDataReader r, string col)
    => r.IsDBNull(col) ? null : r.GetString(col);

        static string? GetStr(OracleDataReader r, int ordinal)
        {
            if (r.IsDBNull(ordinal)) return null;

            var value = r.GetValue(ordinal);
            if (value is OracleClob clob)
                return clob.IsNull ? null : clob.Value;

            return value?.ToString();
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

        public sealed class AdminUserSectionCountDto
        {
            public long Id { get; set; }
            public string Codice { get; set; } = "";
            public string Nome { get; set; } = "";
            public int Count { get; set; }
        }

        public async Task<List<AdminUserSectionCountDto>> GetAdminSectionsForUserAsync(string utente)
        {
            const string sql = @"
SELECT 
    s.ID,
    s.CODICE,
    s.NOME,
    COUNT(DISTINCT NVL(e.THREAD_KEY, TO_CHAR(e.ID))) AS CNT
FROM SGAPP.EMAIL_INBOX_SEZIONI s
JOIN SGAPP.EMAIL_INBOX_SEZIONE_MAP m 
    ON m.ID_SEZIONE = s.ID
JOIN SGAPP.EMAIL_RICEVUTE e 
    ON e.ID = m.ID_EMAIL
WHERE s.ATTIVA = 'Y'
  AND LOWER(m.UTENTE) = LOWER(:p_utente)
  AND NVL(e.ELIMINATO, 'N') <> 'Y'
  AND NVL(e.BLACKLIST, 'N') <> 'Y'
  AND NOT EXISTS (
        SELECT 1
        FROM SGAPP.EMAIL_ARCHIVIO ar
        WHERE ar.ID_EMAIL = e.ID
          AND LOWER(ar.UTENTE) = LOWER(:p_utente)
  )
GROUP BY s.ID, s.CODICE, s.NOME, s.ORDINE
ORDER BY s.ORDINE, s.NOME";

            var result = new List<AdminUserSectionCountDto>();

            await using var conn = new OracleConnection(_connectionString);
            await conn.OpenAsync();

            await using var cmd = new OracleCommand(sql, conn)
            {
                BindByName = true
            };

            cmd.Parameters.Add("p_utente", OracleDbType.Varchar2).Value = utente;

            await using var rd = await cmd.ExecuteReaderAsync();

            while (await rd.ReadAsync())
            {
                result.Add(new AdminUserSectionCountDto
                {
                    Id = rd.GetInt64(0),
                    Codice = rd.IsDBNull(1) ? "" : rd.GetString(1),
                    Nome = rd.IsDBNull(2) ? "" : rd.GetString(2),
                    Count = Convert.ToInt32(rd.GetDecimal(3))
                });
            }

            return result;
        }
        public sealed class HomeSectionCountDto
        {
            public long Id { get; set; }
            public string Codice { get; set; } = "";
            public string Nome { get; set; } = "";
            public int Count { get; set; }
        }

        public async Task<string?> GetLatestCpByUserAsync(string utente)
        {
            const string sql = @"
SELECT ID
FROM (
    SELECT ID
    FROM SGAPP.ACCESSI
    WHERE LOWER(UTENTE) = LOWER(:p_utente)
    ORDER BY DATAIN DESC
)
WHERE ROWNUM = 1";

            await using var conn = new OracleConnection(_connectionString);
            await conn.OpenAsync();

            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
            cmd.Parameters.Add("p_utente", OracleDbType.Varchar2).Value = utente;

            var result = await cmd.ExecuteScalarAsync();
            return result == null || result == DBNull.Value ? null : result.ToString();
        }

        public async Task<int> GetUnreadMentionsCountAsync(string utente)
        {
            const string sql = @"
SELECT COUNT(DISTINCT NVL(e.THREAD_KEY, TO_CHAR(e.ID)))
FROM SGAPP.EMAIL_MENZIONI m
JOIN SGAPP.EMAIL_RICEVUTE e ON e.ID = m.EMAIL_ID
WHERE LOWER(m.UTENTE) = LOWER(:p_utente)
  AND m.VISTO = 'N'
  AND NVL(e.ELIMINATO, 'N') <> 'Y'";

            await using var conn = new OracleConnection(_connectionString);
            await conn.OpenAsync();

            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
            cmd.Parameters.Add("p_utente", OracleDbType.Varchar2).Value = utente;

            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }

        public async Task<List<HomeSectionCountDto>> GetHomeSectionsForUserAsync(string utente)
        {
            const string sql = @"
SELECT 
    s.ID,
    s.CODICE,
    s.NOME,
    COUNT(DISTINCT NVL(e.THREAD_KEY, TO_CHAR(e.ID))) AS CNT
FROM SGAPP.EMAIL_INBOX_SEZIONI s
LEFT JOIN SGAPP.EMAIL_INBOX_SEZIONE_MAP m
       ON m.ID_SEZIONE = s.ID
      AND LOWER(m.UTENTE) = LOWER(:p_utente)
LEFT JOIN SGAPP.EMAIL_RICEVUTE e
       ON e.ID = m.EMAIL_ID
      AND NVL(e.ELIMINATO, 'N') <> 'Y'
WHERE s.ATTIVA = 'Y'
GROUP BY s.ID, s.CODICE, s.NOME, s.ORDINE
ORDER BY s.ORDINE, s.NOME";

            var result = new List<HomeSectionCountDto>();

            await using var conn = new OracleConnection(_connectionString);
            await conn.OpenAsync();

            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
            cmd.Parameters.Add("p_utente", OracleDbType.Varchar2).Value = utente;

            await using var r = await cmd.ExecuteReaderAsync();

            while (await r.ReadAsync())
            {
                result.Add(new HomeSectionCountDto
                {
                    Id = r.GetInt64(0),
                    Codice = r.IsDBNull(1) ? "" : r.GetString(1),
                    Nome = r.IsDBNull(2) ? "" : r.GetString(2),
                    Count = Convert.ToInt32(r.GetValue(3))
                });
            }

            return result;
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
                    .Select(a => new
                    {
                        a.Id,
                        a.NomeFile,
                        a.MimeType,
                        a.IsEmailEml,
                        a.EmailEmlId
                    })
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
                    Allegati = dto.Allegati.Select(a => new AllegatoItem_NEW
                    {
                        Id = a.Id,
                        NomeFile = a.NomeFile,
                        MimeType = a.MimeType,
                        IsEmailEml = string.Equals(a.IsEmailEml, "Y", StringComparison.OrdinalIgnoreCase),
                        EmailEmlId = a.EmailEmlId
                    }).ToList(),
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

        public async Task AddToBlacklistAsync(string email, string utente, int? emailId = null)
        {
            if (string.IsNullOrWhiteSpace(email))
                return;

            email = email.Trim().ToLowerInvariant();

            await using var db = _dbFactory.CreateDbContext();

            var exists = await db.Database.SqlQueryRaw<int>(
                @"SELECT COUNT(*) AS ""Value""
          FROM SGAPP.EMAIL_BLACKLIST
          WHERE LOWER(EMAIL) = :email
            AND NVL(ATTIVA,'Y') = 'Y'",
                new OracleParameter("email", email)
            ).SingleAsync();

            if (exists == 0)
            {
                await db.Database.ExecuteSqlRawAsync(
                    @"INSERT INTO SGAPP.EMAIL_BLACKLIST
              (EMAIL, INSERITO_DA, CREATA_IL, ATTIVA)
              VALUES (:email, :utente, SYSDATE, 'Y')",
                    new OracleParameter("email", email),
                    new OracleParameter("utente", utente)
                );
            }

            if (emailId.HasValue)
            {
                await db.Database.ExecuteSqlRawAsync(
                    @"UPDATE SGAPP.EMAIL_RICEVUTE
              SET BLACKLIST = 'Y'
              WHERE ID = :id",
                    new OracleParameter("id", emailId.Value)
                );
            }
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
            long? draftId,
            string? fromAddress,
            string? threadKey = null,
            string? replyToMessageId = null)
        {
            using var db = _dbFactory.CreateDbContext();

            EmailBozza? bozza = null;

            if (draftId.HasValue)
            {
                bozza = await db.EmailBozze
                    .FirstOrDefaultAsync(x => x.Id == draftId.Value && x.Utente == utente);
            }

            if (bozza == null && !string.IsNullOrWhiteSpace(replyToMessageId))
            {
                bozza = await db.EmailBozze.FirstOrDefaultAsync(x =>
                    x.Utente == utente &&
                    x.ReplyToMessageId == replyToMessageId);
            }

            if (bozza == null && !string.IsNullOrWhiteSpace(threadKey))
            {
                bozza = await db.EmailBozze.FirstOrDefaultAsync(x =>
                    x.Utente == utente &&
                    x.ThreadKey == threadKey);
            }

            if (bozza == null)
            {
                bozza = new EmailBozza
                {
                    Utente = utente
                };

                db.EmailBozze.Add(bozza);
            }

            bozza.Destinatari = to;
            bozza.Cc = cc;
            bozza.Ccn = ccn;
            bozza.Oggetto = subject;
            bozza.CorpoHtml = bodyHtml;
            bozza.CasellaMittente = fromAddress;
            bozza.ThreadKey = threadKey;
            bozza.ReplyToMessageId = replyToMessageId;
            bozza.Letto = false;
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

        public async Task<string> GetFirmaUtenteAsync(
    string username,
    string emailCasella,
    CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();

            var emailNorm = (emailCasella ?? "").Trim().ToLower();
            var usernameNorm = (username ?? "").Trim().ToLower();

            var casella = await db.CasellePosta
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Email.ToLower() == emailNorm, ct);

            if (casella == null)
                return "";

            var abilitazione = await db.CasellaAbilitazioni
                .AsNoTracking()
                .FirstOrDefaultAsync(a =>
                    a.Username.ToLower() == usernameNorm &&
                    a.CasellaId == casella.Id, ct);

            if (abilitazione == null)
                return "";

            var nome = abilitazione.Nome?.Trim();
            var titolo = abilitazione.Titolo?.Trim();
            var recapito = abilitazione.Recapito?.Trim();
            var firmaDefault = casella.FirmaDefault?.Trim();

            var righe = new List<string>
                {
                    "",
                    "Cordiali saluti."
                };

            if (!string.IsNullOrWhiteSpace(nome))
                righe.Add(nome);

            if (!string.IsNullOrWhiteSpace(titolo))
                righe.Add(titolo);

            if (!string.IsNullOrWhiteSpace(recapito))
                righe.Add(recapito);

            if (!string.IsNullOrWhiteSpace(firmaDefault))
            {
                righe.Add("");
                righe.Add($@"<span style=""color:#004080;font-weight:bold;"">{firmaDefault}</span>");
            }

            return string.Join("\n", righe);
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
string? threadKey = null,
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
            var senderDomain = casella.Email.Split('@').LastOrDefault();

            message.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId(
                string.IsNullOrWhiteSpace(senderDomain)
                    ? "grupposantacroce.com"
                    : senderDomain
            );
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

            // ✅ 4️⃣ Header di threading Gmail
            static string CleanMsgId(string? value)
            {
                return (value ?? "")
                    .Trim()
                    .Trim('<', '>', ' ', '\t', '\r', '\n');
            }

            static string AngleMsgId(string? value)
            {
                var clean = CleanMsgId(value);
                return string.IsNullOrWhiteSpace(clean) ? "" : $"<{clean}>";
            }

            var cleanInReplyTo = CleanMsgId(inReplyTo);

            var refs = new List<string>();

            if (!string.IsNullOrWhiteSpace(referencesHdr))
            {
                refs.AddRange(
                    referencesHdr
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Select(CleanMsgId)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                );
            }

            if (!string.IsNullOrWhiteSpace(cleanInReplyTo))
                refs.Add(cleanInReplyTo);

            refs = refs
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!string.IsNullOrWhiteSpace(cleanInReplyTo))
            {
                message.InReplyTo = cleanInReplyTo;
                message.Headers.RemoveAll(HeaderId.InReplyTo);
                message.Headers.Add(HeaderId.InReplyTo, AngleMsgId(cleanInReplyTo));
            }

            message.References.Clear();

            foreach (var r in refs)
                message.References.Add(r);

            message.Headers.RemoveAll(HeaderId.References);

            if (refs.Any())
            {
                message.Headers.Add(
                    HeaderId.References,
                    string.Join(" ", refs.Select(AngleMsgId))
                );
            }

            var messageIdClean = CleanMsgId(message.MessageId);
            var threadKeyClean = NormalizeMsgId(threadKey);

            if (string.IsNullOrWhiteSpace(threadKeyClean) && !string.IsNullOrWhiteSpace(cleanInReplyTo))
            {
                var parentId = NormalizeMsgId(cleanInReplyTo);

                var existingKey = await db.EmailRicevute
                    .Where(e => e.MessageId != null && e.MessageId.ToUpper() == parentId)
                    .Select(e => e.ThreadKey)
                    .FirstOrDefaultAsync(ct);

                if (string.IsNullOrWhiteSpace(existingKey))
                {
                    existingKey = await db.EmailInviate
                        .Where(e => e.MessageId != null && e.MessageId.ToUpper() == parentId)
                        .Select(e => e.ThreadKey)
                        .FirstOrDefaultAsync(ct);
                }

                threadKeyClean = !string.IsNullOrWhiteSpace(existingKey)
                    ? NormalizeMsgId(existingKey)
                    : parentId;
            }

            if (string.IsNullOrWhiteSpace(threadKeyClean))
            {
                threadKeyClean = NormalizeMsgId(messageIdClean);
            }
            var inReplyToClean = string.IsNullOrWhiteSpace(cleanInReplyTo)
    ? null
    : cleanInReplyTo;

            var corpoTesto = Regex.Replace(bodyHtml ?? "", "<.*?>", string.Empty);

            var inviata = new EmailInviata
            {
                CasellaId = casella.Id,
                Utente = casella.Email,
                Destinatari = to,
                Cc = cc,
                Bcc = bcc,
                Oggetto = subject ?? "(nessun oggetto)",
                CorpoHtml = bodyHtml ?? "",
                CorpoTesto = corpoTesto,
                DataInvio = TimeZoneInfo.ConvertTimeFromUtc(
                    DateTime.UtcNow,
                    TimeZoneInfo.FindSystemTimeZoneById(
                        OperatingSystem.IsWindows()
                            ? "W. Europe Standard Time"
                            : "Europe/Rome"
                    )
                ),
                MessageId = messageIdClean,
                InReplyTo = inReplyToClean,
                ReferencesHdr = string.Join(" ", refs),
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
            else if (casella.Provider.Equals("Aruba", StringComparison.OrdinalIgnoreCase)
                  || casella.Provider.Equals("Aruba-IMAPS", StringComparison.OrdinalIgnoreCase))
            {
                if (casella.Provider.Equals("Aruba-IMAPS", StringComparison.OrdinalIgnoreCase)
                    || casella.Email.Contains("@pec.", StringComparison.OrdinalIgnoreCase))
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

            message.Headers.RemoveAll(HeaderId.InReplyTo);
            message.Headers.RemoveAll(HeaderId.References);

            if (!string.IsNullOrWhiteSpace(cleanInReplyTo))
            {
                message.Headers.Add(HeaderId.InReplyTo, $"<{cleanInReplyTo}>");
            }

            if (refs.Any())
            {
                message.Headers.Add(
                    HeaderId.References,
                    string.Join(" ", refs.Select(x => $"<{x}>"))
                );
            }

            _logger.LogWarning("SMTP FINAL In-Reply-To = {Header}", message.Headers["In-Reply-To"]);
            _logger.LogWarning("SMTP FINAL References = {Header}", message.Headers["References"]);
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
        private static string NormalizeMsgId(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            var v = value.Trim()
                .Trim('<', '>', ' ', '\t', '\r', '\n', '|', ';', ',');

            if (v.Contains('|'))
                v = v.Split('|', StringSplitOptions.RemoveEmptyEntries)[0].Trim();

            return v.ToUpperInvariant();
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

            var whereSql = $@"
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

                    whereSql += @"
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

                    whereSql += @"
  AND EXISTS (
        SELECT 1
        FROM SGAPP.EMAIL_ARCHIVIO ar
        WHERE ar.ID_EMAIL = e.ID
          AND ar.UTENTE = :p_utente
  )";
                }
            }

            // filtro word
            if (hasWord)
            {
                whereSql += @"
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

            // filtro address
            if (hasAddr)
            {
                whereSql += @"
  AND (
        LOWER(NVL(e.MITTENTE,''))     LIKE '%' || :p_addr || '%'
     OR LOWER(NVL(e.DESTINATARI,''))  LIKE '%' || :p_addr || '%'
     OR LOWER(NVL(e.CC,''))           LIKE '%' || :p_addr || '%'
     OR LOWER(NVL(e.CCN,''))          LIKE '%' || :p_addr || '%'
  )";
            }

            var sql = $@"
WITH filtered_rows AS (
    SELECT
        e.ID,
        NVL(e.THREAD_KEY, 'R_SINGLE_' || e.ID) AS THREAD_KEY_NORM,
        e.THREAD_KEY,
        e.MITTENTE,
        e.DESTINATARI,
        e.OGGETTO,
        e.DATA_RICEZIONE,
        e.CASELLA_ID,
        c.EMAIL AS CASELLA_EMAIL,
        e.APERTO,
        CASE
            WHEN EXISTS (
                SELECT 1
                FROM SGAPP.EMAIL_LETTURE_UTENTE l
                WHERE l.EMAIL_ID = e.ID
                  AND LOWER(NVL(l.UTENTE, '')) = LOWER(NVL(:p_utente, ''))
            )
            THEN 1
            ELSE 0
        END AS IS_READ_BY_ME,
        e.CC,
        e.CCN,
        CASE WHEN EXISTS (
            SELECT 1
            FROM SGAPP.EMAIL_ALLEGATI a
            WHERE a.EMAIL_ID = e.ID
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
        ) AS ASSEGNATO_A,
        ROW_NUMBER() OVER (
            PARTITION BY NVL(e.THREAD_KEY, 'R_SINGLE_' || e.ID)
            ORDER BY e.DATA_RICEZIONE DESC, e.ID DESC
        ) AS RN,
        MAX(
            CASE
                WHEN EXISTS (
                    SELECT 1
                    FROM SGAPP.EMAIL_LETTURE_UTENTE l
                    WHERE l.EMAIL_ID = e.ID
                      AND LOWER(NVL(l.UTENTE, '')) = LOWER(NVL(:p_utente, ''))
                )
                THEN 0
                ELSE 1
            END
        ) OVER (
            PARTITION BY NVL(e.THREAD_KEY, 'R_SINGLE_' || e.ID)
        ) AS HAS_UNREAD
    FROM SGAPP.EMAIL_RICEVUTE e
    JOIN SGAPP.CASELLEPOSTA c ON c.ID = e.CASELLA_ID
    {whereSql}
),
thread_totals AS (
    SELECT
        THREAD_KEY_NORM,
        SUM(CNT) AS THREAD_LEN_REAL
    FROM (
        SELECT
            r.THREAD_KEY AS THREAD_KEY_NORM,
            COUNT(*) AS CNT
        FROM SGAPP.EMAIL_RICEVUTE r
        WHERE NVL(r.ELIMINATO, 'N') = 'N'
          AND r.THREAD_KEY IN (
              SELECT DISTINCT fr.THREAD_KEY
              FROM filtered_rows fr
              WHERE fr.THREAD_KEY IS NOT NULL
          )
        GROUP BY r.THREAD_KEY

        UNION ALL

        SELECT
            i.THREAD_KEY AS THREAD_KEY_NORM,
            COUNT(*) AS CNT
        FROM SGAPP.EMAIL_INVIATE i
        WHERE i.THREAD_KEY IN (
            SELECT DISTINCT fr.THREAD_KEY
            FROM filtered_rows fr
            WHERE fr.THREAD_KEY IS NOT NULL
        )
        GROUP BY i.THREAD_KEY
    )
    GROUP BY THREAD_KEY_NORM
)
SELECT
    fr.ID,
    fr.THREAD_KEY,
    fr.MITTENTE,
    fr.DESTINATARI,
    fr.OGGETTO,
    fr.DATA_RICEZIONE,
    fr.CASELLA_ID,
    fr.CASELLA_EMAIL,
    CASE WHEN fr.HAS_UNREAD = 1 THEN 'N' ELSE 'Y' END AS APERTO,
    fr.IS_READ_BY_ME,
    fr.CC,
    fr.CCN,
    fr.HAS_ATTACH,
    fr.ATT_PACK,
    fr.PREVIEW,
    fr.ASSEGNATO_A,
    NVL(tt.THREAD_LEN_REAL, 1) AS THREAD_LEN,
    COUNT(*) OVER() AS TOTAL_COUNT
FROM filtered_rows fr
LEFT JOIN thread_totals tt
    ON tt.THREAD_KEY_NORM = fr.THREAD_KEY
WHERE fr.RN = 1
ORDER BY fr.DATA_RICEZIONE DESC, fr.ID DESC
OFFSET :p_offset ROWS FETCH NEXT :p_limit ROWS ONLY";

            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };

            cmd.Parameters.Add("p_utente", OracleDbType.Varchar2).Value = utente ?? string.Empty;

            string? pWord = null;
            string? pAddr = null;

            if (hasWord)
            {
                pWord = w.Trim().ToLowerInvariant().Replace("\\", "");
                cmd.Parameters.Add("p_word", OracleDbType.Varchar2).Value = pWord;
            }

            if (hasAddr)
            {
                pAddr = a.Trim().ToLowerInvariant().Replace("\\", "");
                cmd.Parameters.Add("p_addr", OracleDbType.Varchar2).Value = pAddr;
            }

            cmd.Parameters.Add("p_offset", OracleDbType.Int32).Value = start;
            cmd.Parameters.Add("p_limit", OracleDbType.Int32).Value = pageSize;

            var rawRows = new List<(
      int Id,
      DateTime? Data,
      string Mittente,
      string Oggetto,
      string Aperto,
      bool HasAttachments,
      string? Preview,
      List<AllegatoItem_NEW>? Allegati,
      int CasellaId,
      string? CasellaEmail,
      string? AssegnatoA,
      string? Destinatari,
      string? Cc,
      string? Ccn,
      string? ThreadKey,
      int ThreadLen,
      bool IsReadByCurrentUser
  )>();

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

                rawRows.Add((
                    Id: reader.GetInt32(reader.GetOrdinal("ID")),
                    Data: reader.IsDBNull(reader.GetOrdinal("DATA_RICEZIONE")) ? (DateTime?)null : reader.GetDateTime(reader.GetOrdinal("DATA_RICEZIONE")),
                    Mittente: GetStr(reader, "MITTENTE") ?? "",
                    Oggetto: GetStr(reader, "OGGETTO") ?? "(senza oggetto)",
                    Aperto: GetStr(reader, "APERTO") ?? "",
                    HasAttachments: reader.GetInt32(reader.GetOrdinal("HAS_ATTACH")) == 1,
                    Preview: GetStr(reader, "PREVIEW"),
                    Allegati: allegati,
                    CasellaId: reader.GetInt32(reader.GetOrdinal("CASELLA_ID")),
                    CasellaEmail: GetStr(reader, "CASELLA_EMAIL"),
                    AssegnatoA: GetStr(reader, "ASSEGNATO_A"),
                    Destinatari: GetStr(reader, "DESTINATARI"),
                    Cc: GetStr(reader, "CC"),
                    Ccn: GetStr(reader, "CCN"),
                    ThreadKey: GetStr(reader, "THREAD_KEY"),
                    ThreadLen: reader.IsDBNull(reader.GetOrdinal("THREAD_LEN")) ? 1 : Convert.ToInt32(reader.GetValue(reader.GetOrdinal("THREAD_LEN"))),
                    IsReadByCurrentUser: !reader.IsDBNull(reader.GetOrdinal("IS_READ_BY_ME")) && reader.GetInt32(reader.GetOrdinal("IS_READ_BY_ME")) == 1
                ));
            }

            var list = rawRows.Select(r =>
            {
                var threadLen = r.ThreadLen > 0 ? r.ThreadLen : 1;

                return new EmailListItem_NEW(
                    Id: r.Id,
                    Data: r.Data,
                    Mittente: r.Mittente,
                    Oggetto: r.Oggetto,
                    Aperto: r.Aperto,
                    HasAttachments: r.HasAttachments,
                    ThreadLen: threadLen,
                    Replies: Math.Max(0, threadLen - 1),
                    Preview: r.Preview,
                    Allegati: r.Allegati,
                    MessageId: null,
                    CasellaId: r.CasellaId,
                    CasellaEmail: r.CasellaEmail,
                    AssegnatoA: r.AssegnatoA,
                    Destinatari: r.Destinatari,
                    Cc: r.Cc,
                    Ccn: r.Ccn,
                    ThreadKey: r.ThreadKey,
                    IsReadByCurrentUser: r.IsReadByCurrentUser
                );
            }).ToList();

            return (list, total);
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
            if (casellaIds == null || casellaIds.Count == 0)
            {
                return (new List<EmailListItem_NEW>(), 0);
            }
            var w = (word ?? "").Trim();
            var a = (address ?? "").Trim();

            bool hasWord = !string.IsNullOrWhiteSpace(w) && w.Length >= 2;
            bool hasAddr = !string.IsNullOrWhiteSpace(a) && a.Length >= 2;

            var inList = string.Join(",", casellaIds);

            var whereSql = $@"
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
                whereSql += @"
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
                whereSql += @"
  AND (
        LOWER(NVL(e.MITTENTE,''))      LIKE :addr
     OR LOWER(NVL(e.DESTINATARI,''))   LIKE :addr
     OR LOWER(NVL(e.CC,''))            LIKE :addr
     OR LOWER(NVL(e.CCN,''))           LIKE :addr
  )
";
            }

            var sql = $@"
WITH base_rows AS (
    SELECT
        e.ID,
        e.THREAD_KEY,
        e.MITTENTE,
        e.DESTINATARI,
        e.OGGETTO,
        e.DATA_RICEZIONE,
        e.CASELLA_ID,
        c.EMAIL AS CASELLA_EMAIL,
        e.CC,
        e.CCN,

        CASE WHEN EXISTS (
            SELECT 1 
            FROM SGAPP.EMAIL_ALLEGATI al 
            WHERE al.EMAIL_ID = e.ID
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
        ) AS ASSEGNATO_A,

        ROW_NUMBER() OVER (
            PARTITION BY NVL(e.THREAD_KEY, 'SINGLE_' || e.ID)
            ORDER BY e.DATA_RICEZIONE DESC, e.ID DESC
        ) AS RN,

        MAX(
            CASE 
                WHEN NOT EXISTS (
                    SELECT 1
                    FROM SGAPP.EMAIL_LETTURE_UTENTE l
                    WHERE l.EMAIL_ID = e.ID
                      AND LOWER(l.UTENTE) = LOWER(:utente)
                )
                THEN 1 
                ELSE 0 
            END
        ) OVER (
            PARTITION BY NVL(e.THREAD_KEY, 'SINGLE_' || e.ID)
        ) AS HAS_UNREAD,

        COUNT(*) OVER (
            PARTITION BY NVL(e.THREAD_KEY, 'SINGLE_' || e.ID)
        ) AS THREAD_LEN

    FROM SGAPP.EMAIL_RICEVUTE e
    JOIN SGAPP.CASELLEPOSTA c ON c.ID = e.CASELLA_ID
    {whereSql}
)
SELECT
    ID,
    THREAD_KEY,
    MITTENTE,
    DESTINATARI,
    OGGETTO,
    DATA_RICEZIONE,
    CASELLA_ID,
    CASELLA_EMAIL,
    CASE WHEN HAS_UNREAD = 1 THEN 'N' ELSE 'Y' END AS APERTO,
    CC,
    CCN,
    HAS_ATTACH,
    ATT_PACK,
    PREVIEW,
    ASSEGNATO_A,
    THREAD_LEN,
    COUNT(*) OVER() AS TOTAL_COUNT
FROM base_rows
WHERE RN = 1
ORDER BY DATA_RICEZIONE DESC, ID DESC
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

            var rawRows = new List<(
                int Id,
                DateTime? Data,
                string Mittente,
                string Oggetto,
                string Aperto,
                bool HasAttachments,
                string? Preview,
                List<AllegatoItem_NEW>? Allegati,
                int CasellaId,
                string? CasellaEmail,
                string? AssegnatoA,
                string? Destinatari,
                string? Cc,
                string? Ccn,
                string? ThreadKey
            )>();

            int total = 0;

            await using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    if (total == 0)
                        total = reader.GetInt32(reader.GetOrdinal("TOTAL_COUNT"));

                    string? attPack = reader.IsDBNull(reader.GetOrdinal("ATT_PACK"))
                        ? null
                        : reader.GetString(reader.GetOrdinal("ATT_PACK"));

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
                                var mime = parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2])
                                    ? parts[2]
                                    : null;

                                return new AllegatoItem_NEW(id, nome, mime);
                            })
                            .ToList();
                    }

                    rawRows.Add((
                        Id: reader.GetInt32(reader.GetOrdinal("ID")),
                        Data: reader.IsDBNull(reader.GetOrdinal("DATA_RICEZIONE"))
                            ? null
                            : reader.GetDateTime(reader.GetOrdinal("DATA_RICEZIONE")),
                        Mittente: reader.IsDBNull(reader.GetOrdinal("MITTENTE"))
                            ? ""
                            : reader.GetString(reader.GetOrdinal("MITTENTE")),
                        Oggetto: reader.IsDBNull(reader.GetOrdinal("OGGETTO"))
                            ? ""
                            : reader.GetString(reader.GetOrdinal("OGGETTO")),
                        Aperto: reader.IsDBNull(reader.GetOrdinal("APERTO"))
                            ? "N"
                            : reader.GetString(reader.GetOrdinal("APERTO")),
                        HasAttachments: reader.GetInt32(reader.GetOrdinal("HAS_ATTACH")) == 1,
                        Preview: reader.IsDBNull(reader.GetOrdinal("PREVIEW"))
                            ? null
                            : reader.GetString(reader.GetOrdinal("PREVIEW")),
                        Allegati: allegati,
                        CasellaId: reader.GetInt32(reader.GetOrdinal("CASELLA_ID")),
                        CasellaEmail: reader.IsDBNull(reader.GetOrdinal("CASELLA_EMAIL"))
                            ? null
                            : reader.GetString(reader.GetOrdinal("CASELLA_EMAIL")),
                        AssegnatoA: reader.IsDBNull(reader.GetOrdinal("ASSEGNATO_A"))
                            ? null
                            : reader.GetString(reader.GetOrdinal("ASSEGNATO_A")),
                        Destinatari: GetStr(reader, "DESTINATARI"),
                        Cc: GetStr(reader, "CC"),
                        Ccn: GetStr(reader, "CCN"),
                        ThreadKey: GetStr(reader, "THREAD_KEY")
                    ));
                }
            }

            var threadCounts = await GetRealThreadCountsAsync(conn, rawRows.Select(x => x.Id).ToList());

            var list = rawRows.Select(r =>
            {
                var threadLen = threadCounts.TryGetValue(r.Id, out var len) ? len : 1;

                return new EmailListItem_NEW(
                    Id: r.Id,
                    Data: r.Data,
                    Mittente: r.Mittente,
                    Oggetto: r.Oggetto,
                    Aperto: r.Aperto,
                    HasAttachments: r.HasAttachments,
                    ThreadLen: threadLen,
                    Replies: Math.Max(0, threadLen - 1),
                    Preview: r.Preview,
                    Allegati: r.Allegati,
                    MessageId: null,
                    CasellaId: r.CasellaId,
                    CasellaEmail: r.CasellaEmail,
                    AssegnatoA: r.AssegnatoA,
                    Destinatari: r.Destinatari,
                    Cc: r.Cc,
                    Ccn: r.Ccn,
                    ThreadKey: r.ThreadKey,
                    IsReadByCurrentUser: r.Aperto == "Y"
                );
            }).ToList();

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

            var caselleIds = await db.CasellaAbilitazioni
                .AsNoTracking()
                .Where(a => a.Username == utente)
                .Select(a => a.CasellaId)
                .Distinct()
                .ToListAsync(ct);

            if (caselleIds.Count == 0)
                return new PagedResult<SentEmailListItemDto>(new(), 0);

            var q = db.EmailInviate
                .AsNoTracking()
                .Where(x => caselleIds.Contains(x.CasellaId));

            var s = (searchText ?? "").Trim();

            if (s.Length >= 2)
            {
                var ss = s.ToLower();

                q = q.Where(x =>
                    (x.Oggetto ?? "").ToLower().Contains(ss) ||
                    (x.Destinatari ?? "").ToLower().Contains(ss) ||
                    (x.Cc ?? "").ToLower().Contains(ss) ||
                    (x.Bcc ?? "").ToLower().Contains(ss));
            }

            var total = await q.CountAsync(ct);

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
                    Preview = x.CorpoTesto ?? x.CorpoHtml ?? ""
                })
                .ToListAsync(ct);

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

            return new PagedResult<SentEmailListItemDto>(dto, total);
        }

        public async Task<SentEmailDetailDto?> GetSentDetailAsync(
            int id,
            string utente,
            CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();

            var caselleIds = await db.CasellaAbilitazioni
                .AsNoTracking()
                .Where(a => a.Username == utente)
                .Select(a => a.CasellaId)
                .Distinct()
                .ToListAsync(ct);

            if (caselleIds.Count == 0)
                return null;

            var mail = await db.EmailInviate
                .AsNoTracking()
                .Where(x => x.Id == id && caselleIds.Contains(x.CasellaId))
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

            if (mail == null)
                return null;

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


        public async Task<int> GetConversationCountByThreadAsync(int emailId)
        {
            await using var conn = await GetOpenConnectionAsync();

            string threadKey = "";

            const string findThreadSql = @"
SELECT THREAD_KEY
FROM (
    SELECT THREAD_KEY, 0 AS SRC_PRIORITY
    FROM SGAPP.EMAIL_RICEVUTE
    WHERE ID = :id

    UNION ALL

    SELECT THREAD_KEY, 1 AS SRC_PRIORITY
    FROM SGAPP.EMAIL_INVIATE
    WHERE ID = :id
)
ORDER BY SRC_PRIORITY
FETCH FIRST 1 ROWS ONLY";

            await using (var findCmd = new OracleCommand(findThreadSql, conn))
            {
                findCmd.BindByName = true;
                findCmd.Parameters.Add("id", OracleDbType.Int32).Value = emailId;

                var result = await findCmd.ExecuteScalarAsync();
                threadKey = result?.ToString() ?? "";
            }

            if (string.IsNullOrWhiteSpace(threadKey))
            {
                const string fallbackCountSql = @"
WITH base AS (
    SELECT 0 AS SRC_PRIORITY, THREAD_KEY, MESSAGE_ID, IN_REPLY_TO, REFERENCES_HDR
    FROM SGAPP.EMAIL_RICEVUTE
    WHERE ID = :id

    UNION ALL

    SELECT 1 AS SRC_PRIORITY, THREAD_KEY, MESSAGE_ID, IN_REPLY_TO, REFERENCES_HDR
    FROM SGAPP.EMAIL_INVIATE
    WHERE ID = :id
),
base_one AS (
    SELECT
        TRIM(MESSAGE_ID) AS MESSAGE_ID,
        TRIM(IN_REPLY_TO) AS IN_REPLY_TO,
        REFERENCES_HDR
    FROM (
        SELECT SRC_PRIORITY, MESSAGE_ID, IN_REPLY_TO, REFERENCES_HDR
        FROM base
        ORDER BY SRC_PRIORITY
    )
    WHERE ROWNUM = 1
),
raw_thread AS (
    SELECT 'R' AS TIPO, r.ID
    FROM SGAPP.EMAIL_RICEVUTE r
    CROSS JOIN base_one b
    WHERE NVL(r.ELIMINATO, 'N') = 'N'
      AND (
             UPPER(TRIM(r.MESSAGE_ID)) = UPPER(b.MESSAGE_ID)
          OR UPPER(TRIM(r.IN_REPLY_TO)) = UPPER(b.MESSAGE_ID)
          OR UPPER(TRIM(r.MESSAGE_ID)) = UPPER(b.IN_REPLY_TO)
          OR INSTR(UPPER(NVL(r.REFERENCES_HDR, '')), UPPER(NVL(b.MESSAGE_ID, ''))) > 0
          OR INSTR(UPPER(NVL(r.REFERENCES_HDR, '')), UPPER(NVL(b.IN_REPLY_TO, ''))) > 0
          OR INSTR(UPPER(NVL(b.REFERENCES_HDR, '')), UPPER(TRIM(NVL(r.MESSAGE_ID, '')))) > 0
          OR INSTR(UPPER(NVL(b.REFERENCES_HDR, '')), UPPER(TRIM(NVL(r.IN_REPLY_TO, '')))) > 0
      )

    UNION ALL

    SELECT 'I' AS TIPO, i.ID
    FROM SGAPP.EMAIL_INVIATE i
    CROSS JOIN base_one b
    WHERE (
             UPPER(TRIM(i.MESSAGE_ID)) = UPPER(b.MESSAGE_ID)
          OR UPPER(TRIM(i.IN_REPLY_TO)) = UPPER(b.MESSAGE_ID)
          OR UPPER(TRIM(i.MESSAGE_ID)) = UPPER(b.IN_REPLY_TO)
          OR INSTR(UPPER(NVL(i.REFERENCES_HDR, '')), UPPER(NVL(b.MESSAGE_ID, ''))) > 0
          OR INSTR(UPPER(NVL(i.REFERENCES_HDR, '')), UPPER(NVL(b.IN_REPLY_TO, ''))) > 0
          OR INSTR(UPPER(NVL(b.REFERENCES_HDR, '')), UPPER(TRIM(NVL(i.MESSAGE_ID, '')))) > 0
          OR INSTR(UPPER(NVL(b.REFERENCES_HDR, '')), UPPER(TRIM(NVL(i.IN_REPLY_TO, '')))) > 0
      )
)
SELECT COUNT(*)
FROM (
    SELECT TIPO || ':' || TO_CHAR(ID) AS ROW_KEY
    FROM raw_thread
    GROUP BY TIPO || ':' || TO_CHAR(ID)
)";

                await using var fallbackCmd = new OracleCommand(fallbackCountSql, conn);
                fallbackCmd.BindByName = true;
                fallbackCmd.Parameters.Add("id", OracleDbType.Int32).Value = emailId;

                var fallbackTotal = await fallbackCmd.ExecuteScalarAsync();
                var fallbackCount = fallbackTotal == null ? 0 : Convert.ToInt32(fallbackTotal);
                return fallbackCount <= 0 ? 1 : fallbackCount;
            }

            const string countSql = @"
SELECT COUNT(*)
FROM (
    SELECT ID
    FROM SGAPP.EMAIL_RICEVUTE
    WHERE THREAD_KEY = :p_thread
      AND NVL(ELIMINATO, 'N') = 'N'

    UNION ALL

    SELECT ID
    FROM SGAPP.EMAIL_INVIATE
    WHERE THREAD_KEY = :p_thread
)";

            await using var countCmd = new OracleCommand(countSql, conn);
            countCmd.BindByName = true;
            countCmd.Parameters.Add("p_thread", OracleDbType.Varchar2).Value = threadKey;

            var total = await countCmd.ExecuteScalarAsync();
            return total == null ? 1 : Convert.ToInt32(total);

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
    string? address // lasciato in firma ma non usato
)
        {
            var caselleAdmin = await GetAdminCasellaIdsAsync(utente);
            if (caselleAdmin == null || caselleAdmin.Count == 0)
                return (new List<EmailListItem_NEW>(), 0);

            using var conn = new OracleConnection(_connectionString);
            await conn.OpenAsync();

            var w = (word ?? "").Trim().ToLowerInvariant();
            bool hasWord = !string.IsNullOrWhiteSpace(w) && w.Length >= 2;

            var minDate = GetMailUiMinDate();

            var inList = string.Join(",", caselleAdmin.Select(x => x.ToString()));

            // COUNT thread-based
            var countSql = $@"
SELECT COUNT(*)
FROM (
    SELECT
        ROW_NUMBER() OVER (
            PARTITION BY NVL(e.THREAD_KEY, 'SINGLE_' || e.ID)
            ORDER BY e.DATA_RICEZIONE DESC, e.ID DESC
        ) AS RN
    FROM SGAPP.EMAIL_RICEVUTE e
    WHERE NVL(e.ELIMINATO,'N') = 'N'
      AND e.CASELLA_ID IN ({inList})
      AND NOT EXISTS (
            SELECT 1
            FROM SGAPP.EMAIL_ASSEGNAZIONI x
            WHERE x.EMAIL_ID = e.ID
      )";

            if (minDate.HasValue)
            {
                countSql += @"
      AND e.DATA_RICEZIONE >= :p_minDate";
            }

            if (hasWord)
            {
                countSql += @"
      AND (
            LOWER(NVL(e.OGGETTO,''))   LIKE '%' || :p_word || '%'
         OR LOWER(NVL(e.MITTENTE,'')) LIKE '%' || :p_word || '%'
      )";
            }

            countSql += @"
)
WHERE RN = 1";

            int total;
            await using (var countCmd = new OracleCommand(countSql, conn) { BindByName = true })
            {
                if (minDate.HasValue)
                    countCmd.Parameters.Add("p_minDate", OracleDbType.Date).Value = minDate.Value.Date;

                if (hasWord)
                    countCmd.Parameters.Add("p_word", OracleDbType.Varchar2).Value = w;

                var obj = await countCmd.ExecuteScalarAsync();
                total = Convert.ToInt32(obj);
            }

            if (total == 0)
                return (new List<EmailListItem_NEW>(), 0);

            // LISTA thread-based
            var sql = $@"
WITH ranked AS (
    SELECT
        e.ID,
        e.THREAD_KEY,
        e.MITTENTE,
        e.DESTINATARI,
        e.OGGETTO,
        e.DATA_RICEZIONE,
        e.CASELLA_ID,
        e.APERTO,
        e.CC,
        e.CCN,
        SUBSTR(NVL(e.CORPO_TESTO, e.CORPO_HTML), 1, 200) AS PREVIEW,

        ROW_NUMBER() OVER (
            PARTITION BY NVL(e.THREAD_KEY, 'SINGLE_' || e.ID)
            ORDER BY e.DATA_RICEZIONE DESC, e.ID DESC
        ) AS RN,

        MAX(CASE WHEN NVL(e.APERTO, 'N') = 'N' THEN 1 ELSE 0 END) OVER (
            PARTITION BY NVL(e.THREAD_KEY, 'SINGLE_' || e.ID)
        ) AS HAS_UNREAD,

        COUNT(*) OVER (
            PARTITION BY NVL(e.THREAD_KEY, 'SINGLE_' || e.ID)
        ) AS THREAD_LEN
    FROM SGAPP.EMAIL_RICEVUTE e
    WHERE NVL(e.ELIMINATO,'N') = 'N'
      AND e.CASELLA_ID IN ({inList})
      AND NOT EXISTS (
            SELECT 1
            FROM SGAPP.EMAIL_ASSEGNAZIONI x
            WHERE x.EMAIL_ID = e.ID
      )";

            if (minDate.HasValue)
            {
                sql += @"
      AND e.DATA_RICEZIONE >= :p_minDate";
            }

            if (hasWord)
            {
                sql += @"
      AND (
            LOWER(NVL(e.OGGETTO,''))   LIKE '%' || :p_word || '%'
         OR LOWER(NVL(e.MITTENTE,'')) LIKE '%' || :p_word || '%'
      )";
            }

            sql += @"
),
paged AS (
    SELECT *
    FROM ranked
    WHERE RN = 1
    ORDER BY DATA_RICEZIONE DESC, ID DESC
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
    p.ID,
    p.THREAD_KEY,
    p.MITTENTE,
    p.DESTINATARI,
    p.OGGETTO,
    p.DATA_RICEZIONE,
    p.CASELLA_ID,
    c.EMAIL AS CASELLA_EMAIL,
    CASE WHEN p.HAS_UNREAD = 1 THEN 'N' ELSE 'Y' END AS APERTO,
    p.CC,
    p.CCN,
    NVL(att.HAS_ATTACH,0) AS HAS_ATTACH,
    att.ATT_PACK,
    p.PREVIEW,
    p.THREAD_LEN
FROM paged p
JOIN SGAPP.CASELLEPOSTA c ON c.ID = p.CASELLA_ID
LEFT JOIN att ON att.EMAIL_ID = p.ID
ORDER BY p.DATA_RICEZIONE DESC, p.ID DESC";

            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };

            if (minDate.HasValue)
                cmd.Parameters.Add("p_minDate", OracleDbType.Date).Value = minDate.Value.Date;

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

                var emailId = reader.GetInt32(reader.GetOrdinal("ID"));
                var threadLen = await GetConversationCountByThreadAsync(emailId);

                list.Add(new EmailListItem_NEW(
                    Id: reader.GetInt32(reader.GetOrdinal("ID")),
                    Data: reader.IsDBNull(reader.GetOrdinal("DATA_RICEZIONE"))
                        ? (DateTime?)null
                        : reader.GetDateTime(reader.GetOrdinal("DATA_RICEZIONE")),
                    Mittente: GetStr(reader, "MITTENTE") ?? "",
                    Oggetto: GetStr(reader, "OGGETTO") ?? "(senza oggetto)",
                    Aperto: GetStr(reader, "APERTO") ?? "",
                    HasAttachments: reader.GetInt32(reader.GetOrdinal("HAS_ATTACH")) == 1,
                    ThreadLen: threadLen,
                    Replies: Math.Max(0, threadLen - 1),
                    Preview: GetStr(reader, "PREVIEW"),
                    Allegati: allegati,
                    MessageId: null,
                    CasellaId: reader.IsDBNull(reader.GetOrdinal("CASELLA_ID"))
                        ? null
                        : reader.GetInt32(reader.GetOrdinal("CASELLA_ID")),
                    CasellaEmail: GetStr(reader, "CASELLA_EMAIL"),
                    AssegnatoA: null,
                    Destinatari: GetStr(reader, "DESTINATARI"),
                    Cc: GetStr(reader, "CC"),
                    Ccn: GetStr(reader, "CCN"),
                    ThreadKey: GetStr(reader, "THREAD_KEY")
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
        private static string ExtractEmailOnly(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "";

            var match = Regex.Match(raw, "<([^>]+)>");
            if (match.Success)
                return match.Groups[1].Value.Trim();

            if (raw.Contains("@"))
                return raw.Trim().Trim('"');

            return raw.Trim();
        }

        private static List<string> ParseEmailsSafe(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return new();

            try
            {
                var list = MimeKit.InternetAddressList.Parse(raw);

                return list.Mailboxes
                    .Select(m => (m.Address ?? "").Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();
            }
            catch
            {
                return raw.Split(';', ',', '\n', '\r')
                    .Select(x => ExtractEmailOnly(x))
                    .Where(x => x.Contains("@"))
                    .ToList();
            }
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

        public async Task<EmailReplyInfo?> GetReplyInfoAsync(string? threadKey, string? inReplyTo)
        {
            if (string.IsNullOrWhiteSpace(threadKey) && string.IsNullOrWhiteSpace(inReplyTo))
                return null;

            using var conn = new OracleConnection(_connectionString);
            await conn.OpenAsync();

            // 1️⃣ prima provo su EMAIL_INVIATE
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

            // 2️⃣ fallback su EMAIL_RICEVUTE
            {
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
                    Bcc = r.IsDBNull(3) ? null : r.GetString(3),
                };
            }
        }

        public async Task<bool> CanDeleteEmailAsync(int emailId, string utente)
        {
            const string sql = @"
SELECT COUNT(*)
FROM SGAPP.EMAIL_RICEVUTE e
JOIN SGAPP.CASELLA_ABILITAZIONI ca 
  ON ca.CASELLA_ID = e.CASELLA_ID
WHERE e.ID = :p_email_id
  AND LOWER(TRIM(ca.USERNAME)) = LOWER(TRIM(:p_utente))
  AND NVL(ca.IS_ADMIN, 0) = 1";

            await using var conn = new OracleConnection(_connectionString);
            await conn.OpenAsync();

            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
            cmd.Parameters.Add("p_email_id", OracleDbType.Int32).Value = emailId;
            cmd.Parameters.Add("p_utente", OracleDbType.Varchar2, 255).Value = utente.Trim();

            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            return count > 0;
        }

        public async Task DeleteEmailDeepAsync(int emailId, string utente)
        {
            if (!await CanDeleteEmailAsync(emailId, utente))
                throw new UnauthorizedAccessException("Non sei admin della casella di questa email.");

            await using var conn = new OracleConnection(_connectionString);
            await conn.OpenAsync();

            await using var tx = conn.BeginTransaction();

            try
            {
                async Task Exec(string sql)
                {
                    await using var cmd = new OracleCommand(sql, conn)
                    {
                        BindByName = true,
                        Transaction = tx
                    };

                    cmd.Parameters.Add("p_email_id", OracleDbType.Int32).Value = emailId;
                    await cmd.ExecuteNonQueryAsync();
                }

                await Exec("DELETE FROM SGAPP.EMAIL_LETTURE_UTENTE WHERE EMAIL_ID = :p_email_id");
                await Exec("DELETE FROM SGAPP.EMAIL_ASSEGNAZIONI WHERE EMAIL_ID = :p_email_id");
                await Exec("DELETE FROM SGAPP.EMAIL_THREAD_WATCH WHERE EMAIL_ID = :p_email_id");
                await Exec("DELETE FROM SGAPP.EMAIL_MENZIONI WHERE EMAIL_ID = :p_email_id");
                await Exec("DELETE FROM SGAPP.EMAIL_INBOX_SEZIONE_MAP WHERE ID_EMAIL = :p_email_id");
                await Exec("DELETE FROM SGAPP.COMMENTI_EMAIL WHERE EMAIL_ID = :p_email_id");
                await Exec("DELETE FROM SGAPP.EMAIL_ALLEGATI WHERE EMAIL_ID = :p_email_id");

                await Exec("DELETE FROM SGAPP.EMAIL_RICEVUTE WHERE ID = :p_email_id");
                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        }

      

    }

}
