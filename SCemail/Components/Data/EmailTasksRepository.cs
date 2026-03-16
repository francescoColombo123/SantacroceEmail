using Dapper;
using Oracle.ManagedDataAccess.Client;
using SCemail.Components.Shared;
using System.Data;
using System.Text.RegularExpressions;

using static SCemail.Components.Data.MailService_NEW;

namespace SCemail.Components.Data;

public sealed class EmailTasksRepository : IEmailTasksRepository
{
    private readonly string _connStr;
    public EmailTasksRepository(IConfiguration cfg) => _connStr = cfg.GetConnectionString("OracleDb")!;

    private OracleConnection Open() => new(_connStr);

    // POCO: Dapper con Oracle lo materializza sempre (meglio del record positional)
    private sealed class TaskRow
    {
        public decimal Id { get; set; }
        public string Titolo { get; set; } = "";
        public string? DescrizioneMarkdown { get; set; }
        public string CreatoDa { get; set; } = "";
        public DateTime DataCreazione { get; set; }
        public DateTime? DataScadenza { get; set; }
        public string Stato { get; set; } = "";
        public DateTime? DataChiusura { get; set; }
        public string? ChiusoDa { get; set; }
    }

    private sealed class TaskCommentRow
    {
        public decimal Id { get; set; }
        public decimal TaskId { get; set; }
        public string Utente { get; set; } = "";
        public string Testo { get; set; } = "";
        public DateTime DataCreazione { get; set; }
        public decimal? ReplyTo { get; set; }
    }


    // -----------------------
    // LISTA TASK PER UTENTE
    // -----------------------
    public async Task<List<TaskDto>> GetForUserAsync(string username)
    {
        var u = (username ?? "").Trim();

        const string sql = @"
SELECT
       t.ID               AS Id,
       t.TITOLO           AS Titolo,
       t.DESCRIZIONE_MD   AS DescrizioneMarkdown,
       LOWER(t.CREATO_DA) AS CreatoDa,
       t.DATA_CREAZIONE   AS DataCreazione,
       t.DATA_SCADENZA    AS DataScadenza,
       t.STATO            AS Stato,
       t.DATA_CHIUSURA    AS DataChiusura,
       LOWER(t.CHIUSO_DA) AS ChiusoDa
FROM SGAPP.EMAIL_TASKS t
WHERE LOWER(t.CREATO_DA) = LOWER(:u)
   OR EXISTS (
       SELECT 1
       FROM SGAPP.EMAIL_TASK_PARTICIPANTS p
       WHERE p.TASK_ID = t.ID
         AND LOWER(p.UTENTE) = LOWER(:u)
   )
ORDER BY t.DATA_CREAZIONE DESC";

        await using var con = Open();
        var rows = (await con.QueryAsync<TaskRow>(sql, new { u })).ToList();
        if (rows.Count == 0) return new();

        // base list
        var tasks = rows.Select(r => new TaskDto(
            Id: (int)r.Id,
            EmailId: null, // non esiste in tabella
            Titolo: r.Titolo,
            DescrizioneMarkdown: r.DescrizioneMarkdown,
            CreatoDa: r.CreatoDa,
            DataCreazione: r.DataCreazione,
            DataScadenza: r.DataScadenza,
            Stato: r.Stato,
            DataChiusura: r.DataChiusura,
            ChiusoDa: r.ChiusoDa,
            AssegnatiA: new List<string>()
        )).ToList();

        // partecipanti ASSEGNATO
        var ids = tasks.Select(t => t.Id).Distinct().ToArray();
        if (ids.Length == 0) return tasks;

        var inClause = string.Join(",", ids); // safe: solo numeri

        var map = tasks.ToDictionary(t => t.Id, _ => new List<string>());

        var sqlP = $@"
SELECT TASK_ID AS TaskId,
       LOWER(UTENTE) AS Utente
FROM SGAPP.EMAIL_TASK_PARTICIPANTS
WHERE TASK_ID IN ({inClause})
  AND RUOLO = 'ASSEGNATO'";

        var rowsP = await con.QueryAsync<(decimal TaskId, string Utente)>(sqlP);
        foreach (var r in rowsP)
        {
            var tid = (int)r.TaskId;
            if (map.TryGetValue(tid, out var list))
                list.Add(r.Utente);
        }

        return tasks
            .Select(t => t with
            {
                AssegnatiA = map[t.Id]
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x)
                    .ToList()
            })
            .ToList();
    }

    // -----------------------
    // DETTAGLIO TASK
    // -----------------------
    public async Task<TaskDto?> GetByIdAsync(int id)
    {
        const string sql = @"
SELECT
       t.ID               AS Id,
       t.TITOLO           AS Titolo,
       t.DESCRIZIONE_MD   AS DescrizioneMarkdown,
       LOWER(t.CREATO_DA) AS CreatoDa,
       t.DATA_CREAZIONE   AS DataCreazione,
       t.DATA_SCADENZA    AS DataScadenza,
       t.STATO            AS Stato,
       t.DATA_CHIUSURA    AS DataChiusura,
       LOWER(t.CHIUSO_DA) AS ChiusoDa
FROM SGAPP.EMAIL_TASKS t
WHERE t.ID = :id";

        await using var con = Open();
        var row = await con.QueryFirstOrDefaultAsync<TaskRow>(sql, new { id });
        if (row is null) return null;

        const string sqlAss = @"
SELECT LOWER(UTENTE)
FROM SGAPP.EMAIL_TASK_PARTICIPANTS
WHERE TASK_ID = :id AND RUOLO = 'ASSEGNATO'
ORDER BY LOWER(UTENTE)";

        var ass = (await con.QueryAsync<string>(sqlAss, new { id })).ToList();

        return new TaskDto(
            Id: (int)row.Id,
            EmailId: null,
            Titolo: row.Titolo,
            DescrizioneMarkdown: row.DescrizioneMarkdown,
            CreatoDa: row.CreatoDa,
            DataCreazione: row.DataCreazione,
            DataScadenza: row.DataScadenza,
            Stato: row.Stato,
            DataChiusura: row.DataChiusura,
            ChiusoDa: row.ChiusoDa,
            AssegnatiA: ass
        );
    }

    // -----------------------
    // CREA TASK DA ZERO
    // -----------------------
    public async Task<int> CreateAsync(CreateTaskRequest req)
    {
        if (req is null) throw new ArgumentNullException(nameof(req));

        var creatoDa = (req.CreatoDa ?? "").Trim().ToLowerInvariant();
        var titolo = (req.Titolo ?? "").Trim();
        var descr = string.IsNullOrWhiteSpace(req.DescrizioneMarkdown) ? null : req.DescrizioneMarkdown.Trim();
        var scad = req.DataScadenza;

        if (string.IsNullOrWhiteSpace(creatoDa)) throw new ArgumentException("CreatoDa mancante.");
        if (string.IsNullOrWhiteSpace(titolo)) throw new ArgumentException("Titolo mancante.");

        var assegnati = CleanUsers(req.AssegnatiA);

        await using var con = Open();
        await con.OpenAsync();
        using var tx = con.BeginTransaction();

        try
        {
            // ✅ NO SEQUENCE: ID generato lato app con lock tabella
            var newId = await NextIdWithTableLockAsync(con, tx, "SGAPP.EMAIL_TASKS");

            const string insTask = @"
INSERT INTO SGAPP.EMAIL_TASKS
  (ID, TITOLO, DESCRIZIONE_MD, CREATO_DA, DATA_CREAZIONE, DATA_SCADENZA, STATO)
VALUES
  (:Id, :Titolo, :Descr, :CreatoDa, SYSTIMESTAMP, :Scad, 'APERTO')";

            await con.ExecuteAsync(insTask, new
            {
                Id = newId,
                Titolo = titolo,
                Descr = descr,
                CreatoDa = creatoDa,
                Scad = scad
            }, tx);

            if (assegnati.Count > 0)
            {
                await UpsertAssigneesInternalAsync(con, tx, newId, assegnati);

                await InboxNotifyAsync(
                    con, tx,
                    taskId: newId,
                    recipients: assegnati,
                    eventType: "ASSIGNED",
                    eventText: $"🆕 {creatoDa} ti ha assegnato un task",
                    excludeUser: creatoDa
                );
            }
            tx.Commit();
            return newId;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }


    private async Task UpsertAssigneesInternalAsync(
    OracleConnection con,
    IDbTransaction tx,
    int taskId,
    List<string> assigneesLower)
    {
        // Lock una volta sola per batch (evita rallentamenti)
        await con.ExecuteAsync("LOCK TABLE SGAPP.EMAIL_TASK_PARTICIPANTS IN EXCLUSIVE MODE", transaction: tx);

        const string existsAnyRoleSql = @"
SELECT COUNT(1)
FROM SGAPP.EMAIL_TASK_PARTICIPANTS
WHERE TASK_ID = :TaskId
  AND LOWER(UTENTE) = LOWER(:Utente)";
        const string ins = @"
INSERT INTO SGAPP.EMAIL_TASK_PARTICIPANTS (ID, TASK_ID, UTENTE, RUOLO)
VALUES (:Id, :TaskId, :Utente, :Ruolo)";

        foreach (var u in assigneesLower)
        {
            var exists = await con.ExecuteScalarAsync<int>(
     existsAnyRoleSql,
     new { TaskId = taskId, Utente = u },
     tx
 );

            if (exists > 0) continue;

            // max+1 (tabella già lockata)
            var newPid = await con.ExecuteScalarAsync<decimal>(
                "SELECT NVL(MAX(ID),0) + 1 FROM SGAPP.EMAIL_TASK_PARTICIPANTS",
                transaction: tx
            );

            await con.ExecuteAsync(ins, new
            {
                Id = (int)newPid,
                TaskId = taskId,
                Utente = u,
                Ruolo = "ASSEGNATO"
            }, tx);
        }
    }


    public async Task SetAssigneesAsync(int taskId, List<string> assignees)
    {
        var clean = (assignees ?? new())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        await using var con = Open();
        await con.OpenAsync();
        using var tx = con.BeginTransaction();

        try
        {
            // chi è il creatore (per il testo + exclude)
            const string qCreator = @"SELECT LOWER(CREATO_DA) FROM SGAPP.EMAIL_TASKS WHERE ID = :Id";
            var creator = (await con.ExecuteScalarAsync<string>(qCreator, new { Id = taskId }, tx) ?? "")
                .Trim().ToLowerInvariant();

            // assegnati attuali
            const string qOld = @"
SELECT LOWER(UTENTE)
FROM SGAPP.EMAIL_TASK_PARTICIPANTS
WHERE TASK_ID = :Id AND RUOLO='ASSEGNATO'";
            var old = (await con.QueryAsync<string>(qOld, new { Id = taskId }, tx))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // 1) elimino tutti gli ASSEGNATO attuali
            const string del = @"
DELETE FROM SGAPP.EMAIL_TASK_PARTICIPANTS
WHERE TASK_ID = :Id AND RUOLO = 'ASSEGNATO'";
            await con.ExecuteAsync(del, new { Id = taskId }, tx);

            // 2) reinserisco quelli nuovi
            if (clean.Count > 0)
                await UpsertAssigneesInternalAsync(con, tx, taskId, clean);

            // ✅ nuovi assegnati = clean - old
            var added = clean.Where(u => !old.Contains(u)).ToList();
            if (added.Count > 0)
            {
                await InboxNotifyAsync(con, tx, taskId,
                    recipients: added,
                    eventType: "ASSIGNED",
                    eventText: $"🆕 {creator} ti ha assegnato un task",
                    excludeUser: creator);
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    // compat (se non hai EmailId nel DB)
    public Task<int> CreateFromEmailAsync(
        int emailId,
        string creatoDa,
        string? titolo,
        string? descrizioneMarkdown,
        DateTime? dataScadenza,
        List<string> assegnatiA)
    {
        var req = new CreateTaskRequest(
            CreatoDa: creatoDa,
            Titolo: string.IsNullOrWhiteSpace(titolo) ? "(senza titolo)" : titolo!,
            DescrizioneMarkdown: descrizioneMarkdown,
            DataScadenza: dataScadenza,
            AssegnatiA: assegnatiA ?? new(),
            EmailId: null
        );

        return CreateAsync(req);
    }

    // -----------------------
    // CHIUSURA TASK
    // -----------------------
    public async Task CloseAsync(int taskId, CloseTaskRequest req)
    {
        if (req is null) throw new ArgumentNullException(nameof(req));

        var user = (req.Utente ?? "").Trim().ToLowerInvariant();
        var comment = req.Commento?.Trim();

        if (string.IsNullOrWhiteSpace(user))
            throw new ArgumentException("Utente mancante.");

        await using var con = Open();
        await con.OpenAsync();
        using var tx = con.BeginTransaction();

        try
        {
            const string q = @"SELECT LOWER(CREATO_DA) AS CreatoDa, STATO AS Stato
                               FROM SGAPP.EMAIL_TASKS WHERE ID = :Id";
            var row = await con.QueryFirstOrDefaultAsync<(string CreatoDa, string Stato)>(q, new { Id = taskId }, tx);

            if (string.IsNullOrWhiteSpace(row.CreatoDa))
                throw new InvalidOperationException("Task non trovato.");

            if (string.Equals(row.Stato, TaskStati.Chiuso, StringComparison.OrdinalIgnoreCase))
            {
                tx.Commit();
                return;
            }

            var isCreator = string.Equals(row.CreatoDa, user, StringComparison.OrdinalIgnoreCase);

            const string qAss = @"
SELECT COUNT(1)
FROM SGAPP.EMAIL_TASK_PARTICIPANTS
WHERE TASK_ID = :Id AND LOWER(UTENTE) = LOWER(:U) AND RUOLO = 'ASSEGNATO'";
            var cnt = await con.ExecuteScalarAsync<int>(qAss, new { Id = taskId, U = user }, tx);
            var isAssigned = cnt > 0;

            if (!isCreator && !isAssigned)
                throw new UnauthorizedAccessException("Non sei autorizzato a chiudere questo task.");

            if (!isCreator && isAssigned && string.IsNullOrWhiteSpace(comment))
                throw new InvalidOperationException("Per chiudere il task devi inserire un commento.");

            const string upd = @"
UPDATE SGAPP.EMAIL_TASKS
SET STATO = 'CHIUSO',
    DATA_CHIUSURA = SYSTIMESTAMP,
    CHIUSO_DA = :U
WHERE ID = :Id AND STATO <> 'CHIUSO'";
            await con.ExecuteAsync(upd, new { Id = taskId, U = user }, tx);
            var (creator, threadUsers) = await GetThreadUsersAsync(con, tx, taskId);

            await InboxNotifyAsync(con, tx, taskId,
                recipients: threadUsers,
                eventType: "CLOSED",
                eventText: $"🔒 chiuso da {user}",
                excludeUser: user);
            if (!string.IsNullOrWhiteSpace(comment))
                await AddCommentInternalAsync(con, tx, taskId, user, comment!, replyTo: null);

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task DeleteAsync(int id)
    {
        const string sql = @"DELETE FROM SGAPP.EMAIL_TASKS WHERE ID = :id";
        await using var con = Open();
        await con.ExecuteAsync(sql, new { id });
    }

    // -----------------------
    // COMMENTI
    // -----------------------
    public async Task<List<TaskCommentDto>> GetCommentsAsync(int taskId)
    {
        const string sql = @"
SELECT ID             AS Id,
       TASK_ID        AS TaskId,
       LOWER(UTENTE)  AS Utente,
       TESTO          AS Testo,
       DATA_CREAZIONE AS DataCreazione,
       REPLY_TO       AS ReplyTo
FROM SGAPP.EMAIL_TASK_COMMENTS
WHERE TASK_ID = :t
ORDER BY DATA_CREAZIONE ASC";

        await using var con = Open();

        var rows = (await con.QueryAsync<TaskCommentRow>(sql, new { t = taskId })).ToList();

        return rows.Select(r => new TaskCommentDto(
            Id: (int)r.Id,
            TaskId: (int)r.TaskId,
            Utente: r.Utente,
            Testo: r.Testo,
            DataCreazione: r.DataCreazione,
            ReplyTo: r.ReplyTo is null ? null : (int?)r.ReplyTo
        )).ToList();
    }


    private static readonly Regex MentionRx =
        new(@"(?<=^|\s)@([a-zA-Z0-9_.-]+)\b", RegexOptions.Compiled);

    private async Task UpsertMentionedParticipantsAsync(OracleConnection con, IDbTransaction tx, int taskId, string testo)
    {
        var mentions = MentionRx.Matches(testo ?? "")
            .Select(m => m.Groups[1].Value.Trim().ToLowerInvariant())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (mentions.Length == 0) return;

        await con.ExecuteAsync("LOCK TABLE SGAPP.EMAIL_TASK_PARTICIPANTS IN EXCLUSIVE MODE", transaction: tx);

        const string existsSql = @"
SELECT COUNT(1)
FROM SGAPP.EMAIL_TASK_PARTICIPANTS
WHERE TASK_ID = :TaskId
  AND LOWER(UTENTE) = LOWER(:Utente)
  AND RUOLO = :Ruolo";

        const string ins = @"
INSERT INTO SGAPP.EMAIL_TASK_PARTICIPANTS (ID, TASK_ID, UTENTE, RUOLO)
VALUES (:Id, :TaskId, :Utente, :Ruolo)";

        foreach (var m in mentions)
        {
            var exists = await con.ExecuteScalarAsync<int>(
                existsSql,
                new { TaskId = taskId, Utente = m, Ruolo = "OSSERVATORE" },
                tx
            );

            if (exists > 0) continue;

            var newPid = await con.ExecuteScalarAsync<decimal>(
                "SELECT NVL(MAX(ID),0) + 1 FROM SGAPP.EMAIL_TASK_PARTICIPANTS",
                transaction: tx
            );

            await con.ExecuteAsync(ins, new
            {
                Id = (int)newPid,
                TaskId = taskId,
                Utente = m,
                Ruolo = "OSSERVATORE"
            }, tx);
        }
    }


    private async Task<int> AddCommentInternalAsync(
      OracleConnection con,
      IDbTransaction tx,
      int taskId,
      string utente,
      string testo,
      int? replyTo)
    {
        var newId = await NextIdWithTableLockAsync(con, tx, "SGAPP.EMAIL_TASK_COMMENTS");

        const string ins = @"
INSERT INTO SGAPP.EMAIL_TASK_COMMENTS (ID, TASK_ID, UTENTE, TESTO, REPLY_TO, DATA_CREAZIONE)
VALUES (:Id, :t, :u, :txt, :r, SYSTIMESTAMP)";

        await con.ExecuteAsync(ins, new
        {
            Id = newId,
            t = taskId,
            u = utente,
            txt = testo,
            r = replyTo
        }, tx);

        return newId;
    }

    private static async Task<int> NextIdWithTableLockAsync(
    OracleConnection con,
    IDbTransaction tx,
    string fullTableName)
    {
        // Serializza la generazione ID: evita collisioni (MAX+1)
        await con.ExecuteAsync($"LOCK TABLE {fullTableName} IN EXCLUSIVE MODE", transaction: tx);

        var next = await con.ExecuteScalarAsync<decimal>(
            $"SELECT NVL(MAX(ID),0) + 1 FROM {fullTableName}",
            transaction: tx
        );

        return (int)next;
    }

    private static List<string> CleanUsers(IEnumerable<string>? users)
        => (users ?? Enumerable.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private const string InboxUpsertSql = @"
MERGE INTO SGAPP.EMAIL_TASK_INBOX t
USING (
  SELECT :TaskId AS TASK_ID,
         :Utente AS UTENTE,
         :EType  AS LAST_EVENT_TYPE,
         :EText  AS LAST_EVENT_TEXT
  FROM dual
) s
ON (t.TASK_ID = s.TASK_ID AND LOWER(TRIM(t.UTENTE)) = LOWER(TRIM(s.UTENTE)))
WHEN MATCHED THEN
  UPDATE SET
    t.UNREAD_COUNT    = t.UNREAD_COUNT + 1,
    t.LAST_EVENT_AT   = SYSTIMESTAMP,
    t.LAST_EVENT_TYPE = s.LAST_EVENT_TYPE,
    t.LAST_EVENT_TEXT = s.LAST_EVENT_TEXT
WHEN NOT MATCHED THEN
  INSERT (ID, TASK_ID, UTENTE, UNREAD_COUNT, LAST_EVENT_AT, LAST_EVENT_TYPE, LAST_EVENT_TEXT)
  VALUES (SGAPP.SEQ_EMAIL_TASK_INBOX.NEXTVAL, s.TASK_ID, s.UTENTE, 1, SYSTIMESTAMP, s.LAST_EVENT_TYPE, s.LAST_EVENT_TEXT)";

    private const string InboxMarkSeenSql = @"
UPDATE SGAPP.EMAIL_TASK_INBOX
SET UNREAD_COUNT = 0
WHERE TASK_ID = :TaskId
  AND LOWER(TRIM(UTENTE)) = LOWER(TRIM(:Utente))";

    private async Task InboxNotifyAsync(
        OracleConnection con,
        IDbTransaction tx,
        int taskId,
        IEnumerable<string> recipients,
        string eventType,
        string eventText,
        string? excludeUser = null)
    {
        var ex = (excludeUser ?? "").Trim().ToLowerInvariant();

        var list = (recipients ?? Enumerable.Empty<string>())
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u.Trim().ToLowerInvariant())
            .Where(u => string.IsNullOrWhiteSpace(ex) || !string.Equals(u, ex, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (list.Count == 0) return;

        var et = (eventType ?? "").Trim();
        var txt = (eventText ?? "").Trim();
        if (txt.Length > 1000) txt = txt[..1000];

        foreach (var u in list)
        {
            await con.ExecuteAsync(InboxUpsertSql, new
            {
                TaskId = taskId,
                Utente = u,
                EType = et,
                EText = txt
            }, tx);
        }
    }

    private async Task<(string Creator, List<string> ThreadUsers)> GetThreadUsersAsync(
    OracleConnection con,
    IDbTransaction tx,
    int taskId)
    {
        const string qCreator = @"SELECT LOWER(CREATO_DA) FROM SGAPP.EMAIL_TASKS WHERE ID = :Id";
        var creator = await con.ExecuteScalarAsync<string>(qCreator, new { Id = taskId }, tx);
        creator = (creator ?? "").Trim().ToLowerInvariant();

        const string qParts = @"
SELECT LOWER(UTENTE) AS Utente
FROM SGAPP.EMAIL_TASK_PARTICIPANTS
WHERE TASK_ID = :Id";

        var users = (await con.QueryAsync<string>(qParts, new { Id = taskId }, tx))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!string.IsNullOrWhiteSpace(creator) && !users.Contains(creator, StringComparer.OrdinalIgnoreCase))
            users.Add(creator);

        return (creator, users);
    }

    public async Task MarkSeenAsync(int taskId, string utente)
    {
        var u = (utente ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(u)) return;

        await using var con = Open();
        await con.ExecuteAsync(InboxMarkSeenSql, new { TaskId = taskId, Utente = u });
    }
    public async Task<int> AddCommentAsync(int taskId, string utente, string testo, int? replyTo)
    {
        var user = (utente ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(user)) throw new ArgumentException("Utente mancante.");
        if (string.IsNullOrWhiteSpace(testo)) throw new ArgumentException("Testo mancante.");

        await using var con = Open();
        await con.OpenAsync();
        using var tx = con.BeginTransaction();

        try
        {
            var newId = await AddCommentInternalAsync(con, tx, taskId, user, testo, replyTo);

            // 1) se ci sono @mention -> diventano OSSERVATORE
            await UpsertMentionedParticipantsAsync(con, tx, taskId, testo);

            // 2) destinatari thread (creator + partecipanti)
            var (creator, threadUsers) = await GetThreadUsersAsync(con, tx, taskId);

            // 3) regola FATTO:
            //    - se NON sei il creatore => avvisa SOLO il creatore
            //    - se sei il creatore (task personale) => non notificare DONE
            if (IsDoneComment(testo) && !string.IsNullOrWhiteSpace(creator)
                && !string.Equals(creator, user, StringComparison.OrdinalIgnoreCase))
            {
                await InboxNotifyAsync(con, tx, taskId,
                    recipients: new[] { creator },
                    eventType: "DONE",
                    eventText: $"✅ {user}: fatto",
                    excludeUser: user);
            }
            else
            {
                // commento normale -> notifica tutti nel thread tranne autore
                var preview = testo.Trim();
                if (preview.Length > 200) preview = preview[..200] + "…";

                await InboxNotifyAsync(con, tx, taskId,
                    recipients: threadUsers,
                    eventType: "COMMENT",
                    eventText: $"{user}: {preview}",
                    excludeUser: user);
            }

            tx.Commit();
            return newId;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }
    private sealed class BadgeRow
    {
        public decimal TaskId { get; set; }
        public string Titolo { get; set; } = "";
    }

    public async Task<TaskHomeBadgesDto> GetHomeBadgesAsync(string utente)
    {
        var u = (utente ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(u))
            return new TaskHomeBadgesDto(); // class con proprietà

        await using var con = Open();

        // -------------------
        // UNREAD (notifiche task per utente) da EMAIL_TASK_INBOX
        // -------------------
        const string qUnreadTotal = @"
SELECT NVL(SUM(UNREAD_COUNT),0)
FROM SGAPP.EMAIL_TASK_INBOX i
WHERE LOWER(TRIM(i.UTENTE)) = LOWER(TRIM(:u))
  AND (i.LAST_EVENT_TYPE IS NULL OR i.LAST_EVENT_TYPE <> 'ASSIGNED')";

        var unreadTotal = await con.ExecuteScalarAsync<int>(qUnreadTotal, new { u });

        const string qUnreadTop = @"
SELECT *
FROM (
  SELECT
      i.TASK_ID           AS TaskId,
      t.TITOLO            AS Titolo,
      t.DATA_CREAZIONE    AS DataCreazione,
      t.DATA_SCADENZA     AS DataScadenza,
      LOWER(t.CREATO_DA)  AS CreatoDa,
      i.UNREAD_COUNT      AS UnreadCount,
      i.LAST_EVENT_TEXT   AS LastEventText,
      i.LAST_EVENT_AT     AS LastEventAt
  FROM SGAPP.EMAIL_TASK_INBOX i
  JOIN SGAPP.EMAIL_TASKS t ON t.ID = i.TASK_ID
  WHERE LOWER(TRIM(i.UTENTE)) = LOWER(TRIM(:u))
    AND i.UNREAD_COUNT > 0
        AND (i.LAST_EVENT_TYPE IS NULL OR i.LAST_EVENT_TYPE <> 'ASSIGNED')
  ORDER BY i.LAST_EVENT_AT DESC
)
WHERE ROWNUM <= 3";

        var unreadTop = (await con.QueryAsync<TaskHomeItemDto>(qUnreadTop, new { u })).ToList();
        // -------------------
        // TO CLOSE (creati da me + un assegnato ha scritto "Fatto")
        // -------------------
        const string qToCloseCount = @"
SELECT COUNT(DISTINCT t.ID)
FROM SGAPP.EMAIL_TASKS t
WHERE t.STATO = 'APERTO'
  AND LOWER(t.CREATO_DA) = LOWER(:u)
  AND EXISTS (
      SELECT 1
      FROM SGAPP.EMAIL_TASK_PARTICIPANTS p
      JOIN SGAPP.EMAIL_TASK_COMMENTS c
        ON c.TASK_ID = t.ID
       AND LOWER(c.UTENTE)=LOWER(p.UTENTE)
      WHERE p.TASK_ID = t.ID
        AND p.RUOLO = 'ASSEGNATO'
        AND REGEXP_LIKE(LOWER(c.TESTO), '(^|\s)(fatto|done|completato|completata)(\b|$)')
  )";

        var toCloseCount = await con.ExecuteScalarAsync<int>(qToCloseCount, new { u });

        const string qToCloseTop = @"
SELECT *
FROM (
  SELECT t.ID AS TaskId,
         t.TITOLO AS Titolo,
         t.DATA_CREAZIONE AS DataCreazione,
         t.DATA_SCADENZA AS DataScadenza,
         LOWER(t.CREATO_DA) AS CreatoDa,
         MAX(c.DATA_CREAZIONE) AS LastEventAt
  FROM SGAPP.EMAIL_TASKS t
  JOIN SGAPP.EMAIL_TASK_PARTICIPANTS p ON p.TASK_ID=t.ID AND p.RUOLO='ASSEGNATO'
  JOIN SGAPP.EMAIL_TASK_COMMENTS c ON c.TASK_ID=t.ID AND LOWER(c.UTENTE)=LOWER(p.UTENTE)
  WHERE t.STATO='APERTO'
    AND LOWER(t.CREATO_DA)=LOWER(:u)
    AND REGEXP_LIKE(LOWER(c.TESTO), '(^|\s)(fatto|done|completato|completata)(\b|$)')
  GROUP BY t.ID, t.TITOLO, t.DATA_CREAZIONE, t.DATA_SCADENZA, t.CREATO_DA
  ORDER BY MAX(c.DATA_CREAZIONE) DESC
)
WHERE ROWNUM <= 3";

        // mappo già su TaskHomeItemDto (così non tocchi dynamic)
        var toCloseTop = (await con.QueryAsync<TaskHomeItemDto>(qToCloseTop, new { u }))
            .Select(x =>
            {
                x.LastEventText = "✅ pronto da chiudere";
                x.UnreadCount = 0;
                return x;
            })
            .ToList();

        // -------------------
        // WAITING (assegnato a me + io ho scritto "Fatto" + creato da altri)
        // -------------------
        const string qWaitingCount = @"
SELECT COUNT(DISTINCT t.ID)
FROM SGAPP.EMAIL_TASKS t
JOIN SGAPP.EMAIL_TASK_PARTICIPANTS p ON p.TASK_ID=t.ID AND p.RUOLO='ASSEGNATO'
WHERE t.STATO='APERTO'
  AND LOWER(p.UTENTE)=LOWER(:u)
  AND LOWER(t.CREATO_DA) <> LOWER(:u)
  AND EXISTS (
     SELECT 1
     FROM SGAPP.EMAIL_TASK_COMMENTS c
     WHERE c.TASK_ID=t.ID
       AND LOWER(c.UTENTE)=LOWER(:u)
       AND REGEXP_LIKE(LOWER(c.TESTO), '(^|\s)(fatto|done|completato|completata)(\b|$)')
  )";

        var waitingCount = await con.ExecuteScalarAsync<int>(qWaitingCount, new { u });

        const string qWaitingTop = @"
SELECT *
FROM (
  SELECT t.ID AS TaskId,
         t.TITOLO AS Titolo,
         t.DATA_CREAZIONE AS DataCreazione,
         t.DATA_SCADENZA AS DataScadenza,
         LOWER(t.CREATO_DA) AS CreatoDa,
         MAX(c.DATA_CREAZIONE) AS LastEventAt
  FROM SGAPP.EMAIL_TASKS t
  JOIN SGAPP.EMAIL_TASK_PARTICIPANTS p ON p.TASK_ID=t.ID AND p.RUOLO='ASSEGNATO'
  JOIN SGAPP.EMAIL_TASK_COMMENTS c ON c.TASK_ID=t.ID AND LOWER(c.UTENTE)=LOWER(:u)
  WHERE t.STATO='APERTO'
    AND LOWER(p.UTENTE)=LOWER(:u)
    AND LOWER(t.CREATO_DA) <> LOWER(:u)
    AND REGEXP_LIKE(LOWER(c.TESTO), '(^|\s)(fatto|done|completato|completata)(\b|$)')
  GROUP BY t.ID, t.TITOLO, t.DATA_CREAZIONE, t.DATA_SCADENZA, t.CREATO_DA
  ORDER BY MAX(c.DATA_CREAZIONE) DESC
)
WHERE ROWNUM <= 3";

        var waitingTop = (await con.QueryAsync<TaskHomeItemDto>(qWaitingTop, new { u }))
            .Select(x =>
            {
                x.LastEventText = "⏳ in attesa di chiusura";
                x.UnreadCount = 0;
                return x;
            })
            .ToList();

        const string qAssignedTotal = @"
SELECT NVL(SUM(UNREAD_COUNT),0)
FROM SGAPP.EMAIL_TASK_INBOX
WHERE LOWER(TRIM(UTENTE)) = LOWER(TRIM(:u))
  AND LAST_EVENT_TYPE = 'ASSIGNED'";

        var assignedTotal = await con.ExecuteScalarAsync<int>(qAssignedTotal, new { u });

        const string qAssignedTop = @"
SELECT *
FROM (
  SELECT
      i.TASK_ID           AS TaskId,
      t.TITOLO            AS Titolo,
      t.DATA_CREAZIONE    AS DataCreazione,
      t.DATA_SCADENZA     AS DataScadenza,
      LOWER(t.CREATO_DA)  AS CreatoDa,
      i.UNREAD_COUNT      AS UnreadCount,
      i.LAST_EVENT_TEXT   AS LastEventText,
      i.LAST_EVENT_AT     AS LastEventAt
  FROM SGAPP.EMAIL_TASK_INBOX i
  JOIN SGAPP.EMAIL_TASKS t ON t.ID = i.TASK_ID
  WHERE LOWER(TRIM(i.UTENTE)) = LOWER(TRIM(:u))
    AND i.UNREAD_COUNT > 0
    AND i.LAST_EVENT_TYPE = 'ASSIGNED'
  ORDER BY i.LAST_EVENT_AT DESC
)
WHERE ROWNUM <= 3";

        var assignedTop = (await con.QueryAsync<TaskHomeItemDto>(qAssignedTop, new { u })).ToList();

        // -------------------
        // RETURN DTO (class -> object initializer)
        // -------------------
        return new TaskHomeBadgesDto
        {
            UnreadTotal = unreadTotal,
            ToCloseCount = toCloseCount,
            WaitingReviewCount = waitingCount,
            UnreadTop = unreadTop,
            ToCloseTop = toCloseTop,
            WaitingReviewTop = waitingTop,
            AssignedUnreadTotal = assignedTotal,
            AssignedTop = assignedTop,
        };
    }

    private static bool IsDoneComment(string? txt)
    {
        if (string.IsNullOrWhiteSpace(txt)) return false;

        var t = txt.Trim();

        if (t.StartsWith("Fatto", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.StartsWith("Done", StringComparison.OrdinalIgnoreCase)) return true;

        return Regex.IsMatch(t, @"\b(fatto|done|completato|completata)\b", RegexOptions.IgnoreCase);
    }


}
