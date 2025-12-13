namespace SCemail.Components.Shared
{
    public class EmailArchivio
    {
        public int IdArchivio { get; set; }
        public int IdEmail { get; set; }
        public string Utente { get; set; } = "";
        public DateTime DataArchiviazione { get; set; }
        public string? Note { get; set; }
    }
}
