namespace SCemail.Components.Data
{
    public class AccessiService
    {
        private readonly MailDbContext _db;

        public AccessiService(MailDbContext db)
        {
            _db = db;
        }

        public string? GetUtenteFromCp(string? cp)
        {
            if (string.IsNullOrWhiteSpace(cp))
                return null;

            var now = DateTime.Now;

            var accesso = _db.ACCESSI
                .Where(a => a.ID == cp && a.TIPO == 0)
                .OrderByDescending(a => a.DATAIN)
                .FirstOrDefault();

            if (accesso == null)
                return null;

            if ((now - accesso.DATAIN).TotalHours > 8)
                return null;

            return accesso.UTENTE;
        }
    }
}
