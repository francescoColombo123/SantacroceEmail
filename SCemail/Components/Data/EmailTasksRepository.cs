using Dapper;
using Oracle.ManagedDataAccess.Client;
using SCemail.Components.Shared;
using System.Data;
using System.Text.RegularExpressions;

namespace SCemail.Components.Data;

public interface IEmailTasksRepository
{
    Task<IEnumerable<UserTaskItem>> GetForUserAsync(string username);
    Task<UserTaskItem?> GetByIdAsync(int id);
    Task<int> CreateFromEmailAsync(int emailId, string utente, string? commento, string? titolo);
    Task UpdateCommentAsync(int id, string? commento);
    Task DeleteAsync(int id);
    Task CloseAsync(int id, string utente);

    // Commenti
    Task<IEnumerable<TaskComment>> GetCommentsAsync(int taskId);
    Task<int> AddCommentAsync(int taskId, string utente, string testo, int? replyTo);
}

public sealed class EmailTasksRepository : IEmailTasksRepository
{
    private readonly string _connStr;

    public EmailTasksRepository(IConfiguration cfg) => _connStr = cfg.GetConnectionString("OracleDb")!;

    // Task dell'utente: assegnati a lui OPPURE dove è partecipante (menzionato)
    public async Task<IEnumerable<UserTaskItem>> GetForUserAsync(string username)
    {
        const string sql = @"
            select t.ID,
                   t.EMAIL_ID        as EmailId,
                   t.TITOLO          as Titolo,
                   t.STATO           as Stato,
                   t.COMMENTO        as Commento,
                   t.DATA_CREAZIONE  as DataCreazione
              from SGAPP.EMAIL_TASKS t
             where upper(t.UTENTE) = upper(:u)
                or exists (select 1
                             from SGAPP.EMAIL_TASK_PARTICIPANTS p
                            where p.TASK_ID = t.ID
                              and upper(p.UTENTE) = upper(:u))
             order by t.DATA_CREAZIONE desc";
        await using var con = new OracleConnection(_connStr);
        return (await con.QueryAsync<UserTaskItem>(sql, new { u = username })).ToList();
    }

    public async Task<UserTaskItem?> GetByIdAsync(int id)
    {
        const string sql = @"
            select ID, EMAIL_ID as EmailId, UTENTE, COMMENTO, DATA_CREAZIONE as DataCreazione,
                   TITOLO, STATO, DATA_CHIUSURA as DataChiusura
              from SGAPP.EMAIL_TASKS
             where ID = :id";
        await using var con = new OracleConnection(_connStr);
        return await con.QueryFirstOrDefaultAsync<UserTaskItem>(sql, new { id });
    }

    public async Task<int> CreateFromEmailAsync(int emailId, string utente, string? commento, string? titolo)
    {
        const string sql = @"
            insert into SGAPP.EMAIL_TASKS(EMAIL_ID, UTENTE, COMMENTO, TITOLO)
            values (:e, :u, :c, :t)
            returning ID into :newid";
        await using var con = new OracleConnection(_connStr);
        var p = new DynamicParameters();
        p.Add("e", emailId);
        p.Add("u", utente);
        p.Add("c", commento, DbType.String);
        p.Add("t", titolo);
        p.Add("newid", dbType: DbType.Int32, direction: ParameterDirection.Output);
        await con.ExecuteAsync(sql, p);
        return p.Get<int>("newid");
    }

    public async Task UpdateCommentAsync(int id, string? commento)
    {
        const string sql = @"update SGAPP.EMAIL_TASKS set COMMENTO = :c where ID = :id";
        await using var con = new OracleConnection(_connStr);
        await con.ExecuteAsync(sql, new { c = commento, id });
    }

    public async Task CloseAsync(int id, string utente)
    {
        const string sql = @"
            update SGAPP.EMAIL_TASKS
               set STATO = 'CHIUSO', DATA_CHIUSURA = SYSTIMESTAMP
             where ID = :id and UTENTE = :u";
        await using var con = new OracleConnection(_connStr);
        await con.ExecuteAsync(sql, new { id, u = utente });
    }

    public async Task DeleteAsync(int id)
    {
        const string sql = @"delete from SGAPP.EMAIL_TASKS where ID = :id";
        await using var con = new OracleConnection(_connStr);
        await con.ExecuteAsync(sql, new { id });
    }

    // ----- Commenti -----
    public async Task<IEnumerable<TaskComment>> GetCommentsAsync(int taskId)
    {
        const string sql = @"
            select ID, TASK_ID as TaskId, UTENTE, TESTO,
                   DATA_CREAZIONE as DataCreazione, REPLY_TO as ReplyTo
              from SGAPP.EMAIL_TASK_COMMENTS
             where TASK_ID = :t
             order by DATA_CREAZIONE asc";
        await using var con = new OracleConnection(_connStr);
        return (await con.QueryAsync<TaskComment>(sql, new { t = taskId })).ToList();
    }

    // Salva commento e aggiunge i menzionati come PARTECIPANTI (non cambia l'assegnazione)
    public async Task<int> AddCommentAsync(int taskId, string utente, string testo, int? replyTo)
    {
        // 1) inserisco il commento
        const string ins = @"
            insert into SGAPP.EMAIL_TASK_COMMENTS(TASK_ID, UTENTE, TESTO, REPLY_TO)
            values (:t, :u, :txt, :r)
            returning ID into :newid";
        await using var con = new OracleConnection(_connStr);
        var p = new DynamicParameters();
        p.Add("t", taskId);
        p.Add("u", utente);
        p.Add("txt", testo);
        p.Add("r", replyTo, DbType.Int32);
        p.Add("newid", dbType: DbType.Int32, direction: ParameterDirection.Output);
        await con.ExecuteAsync(ins, p);
        var newId = p.Get<int>("newid");

        // 2) estraggo le menzioni e faccio upsert nei partecipanti
        var rx = new Regex(@"(?<=^|\s)@([a-zA-Z0-9_.-]+)\b", RegexOptions.Compiled);
        var mentions = rx.Matches(testo ?? "")
                         .Select(m => m.Groups[1].Value.Trim())
                         .Where(s => !string.IsNullOrWhiteSpace(s))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .ToArray();

        if (mentions.Length > 0)
        {
            const string merge = @"
                MERGE INTO SGAPP.EMAIL_TASK_PARTICIPANTS p
                USING (SELECT :t TASK_ID, :m UTENTE FROM dual) s
                ON (p.TASK_ID = s.TASK_ID AND upper(p.UTENTE) = upper(s.UTENTE))
                WHEN NOT MATCHED THEN INSERT (TASK_ID, UTENTE) VALUES (s.TASK_ID, s.UTENTE)";
            foreach (var m in mentions)
                await con.ExecuteAsync(merge, new { t = taskId, m });
        }

        return newId;
    }
}
