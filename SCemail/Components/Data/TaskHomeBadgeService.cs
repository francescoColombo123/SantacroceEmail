namespace SCemail.Components.Data
{
    using Microsoft.EntityFrameworkCore;
    using System.Data;

    public sealed class TaskHomeBadgeService
    {
        private readonly IDbContextFactory<MailDbContext> _dbFactory;

        public TaskHomeBadgeService(IDbContextFactory<MailDbContext> dbFactory)
        {
            _dbFactory = dbFactory;
        }

        // (le tue SQL le lascio uguali)
        private const string SQL_TO_CLOSE_COUNT = @" ... ";
        private const string SQL_TO_CLOSE_TOP = @" ... ";
        private const string SQL_WAIT_COUNT = @" ... ";
        private const string SQL_WAIT_TOP = @" ... ";

        public async Task<TaskHomeBadgesDto> GetHomeBadgesAsync(string utente)
        {
            var me = (utente ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(me))
                return new TaskHomeBadgesDto(); // <- OK (class con props)

            await using var db = await _dbFactory.CreateDbContextAsync();
            var conn = db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync();

            async Task<int> ExecScalarInt(string sql)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;

                var p = cmd.CreateParameter();
                p.ParameterName = "me";   // senza :
                p.Value = me;
                cmd.Parameters.Add(p);

                var o = await cmd.ExecuteScalarAsync();
                return (o == null || o == DBNull.Value) ? 0 : Convert.ToInt32(o);
            }

            async Task<List<TaskHomeItemDto>> ExecTop(string sql)
            {
                var list = new List<TaskHomeItemDto>();

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;

                var p = cmd.CreateParameter();
                p.ParameterName = "me";  // senza :
                p.Value = me;
                cmd.Parameters.Add(p);

                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    list.Add(new TaskHomeItemDto
                    {
                        TaskId = Convert.ToInt32(r["ID"]),
                        Titolo = r["TITOLO"]?.ToString() ?? "",
                        DataCreazione = Convert.ToDateTime(r["DATA_CREAZIONE"]),
                        DataScadenza = r["DATA_SCADENZA"] == DBNull.Value
                            ? null
                            : (DateTime?)Convert.ToDateTime(r["DATA_SCADENZA"]),
                        CreatoDa = r["CREATO_DA"]?.ToString() ?? ""
                    });
                }

                return list;
            }

            var toCloseCount = await ExecScalarInt(SQL_TO_CLOSE_COUNT);
            var waitCount = await ExecScalarInt(SQL_WAIT_COUNT);

            var toCloseTop = toCloseCount > 0 ? await ExecTop(SQL_TO_CLOSE_TOP) : new();
            var waitTop = waitCount > 0 ? await ExecTop(SQL_WAIT_TOP) : new();

            // ✅ ritorno con object initializer
            return new TaskHomeBadgesDto
            {
                ToCloseCount = toCloseCount,
                WaitingReviewCount = waitCount,
                ToCloseTop = toCloseTop,
                WaitingReviewTop = waitTop
            };
        }
    }
}