using Microsoft.EntityFrameworkCore;
using Oracle.ManagedDataAccess.Client;

namespace SCemail.Components.Data
{
    public class AccessiService
    {
        private readonly IDbContextFactory<MailDbContext> _factory;

        public AccessiService(IDbContextFactory<MailDbContext> factory)
        {
            _factory = factory;
        }

        public async Task<List<int>> GetCaselleAbilitateAsync(string username, CancellationToken ct = default)
        {
            await using var db = await _factory.CreateDbContextAsync(ct);
            return await db.CasellaAbilitazioni
                .AsNoTracking()
                .Where(a => a.Username == username)
                .Select(a => a.CasellaId)
                .Distinct()
                .ToListAsync(ct);
        }
        public async Task<bool> IsAdminAsync(string username)
        {
            using var db = _factory.CreateDbContext();

            var userUpper = username.ToUpper();

            // CARICO TUTTO IN MEMORIA → Oracle NON PUÒ TOCCARE IsAdmin
            var rows = await db.CasellaAbilitazioni
                .Where(a => a.Username.ToUpper() == userUpper)
                .ToListAsync();

            return rows.Any(r => r.IsAdmin);
        }



        private async Task<OracleConnection> GetOpenConnectionAsync()
        {
            var db = await _factory.CreateDbContextAsync();
            var conn = (OracleConnection)db.Database.GetDbConnection();

            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            return conn;
        }

        public async Task<List<string>> GetUserListAsync()
        {
            var list = new List<string>();

            await using var conn = await GetOpenConnectionAsync();

            const string sql = @"
        SELECT LOWER(UTENTE)
        FROM INFOUSER
        WHERE REGEXP_LIKE(UTENTE, '^[a-z]\.[a-z]+$')
        ORDER BY UTENTE";

            await using var cmd = new OracleCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(reader.GetString(0));
            }

            return list;
        }


        public async Task<string?> GetUtenteFromCpAsync(string? cp, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(cp))
                return null;

            await using var db = await _factory.CreateDbContextAsync(ct);

            var accesso = await db.ACCESSI
                .AsNoTracking()
                .Where(a => a.ID == cp && a.TIPO == 0)
                .OrderByDescending(a => a.DATAIN)
                .FirstOrDefaultAsync(ct);

            return accesso?.UTENTE;
        }
    }
}
