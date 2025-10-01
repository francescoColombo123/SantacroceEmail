namespace SCemail.Components.Shared
{
    public class EmailRicevuta
    {
        public int Id { get; set; }
        public int CasellaId { get; set; }
        public string MessageId { get; set; } = default!;
        public DateTime? DataRicezione { get; set; }
        public string? Mittente { get; set; }
        public string? Destinatari { get; set; }
        public string? Oggetto { get; set; }
        public string? CorpoHtml { get; set; }
        public string? CorpoTesto { get; set; }
        public string? Aperto { get; set; }
        public string? Eliminato { get; set; }

        // NUOVI
        public string? FolderPath { get; set; }
        public long? MessageUid { get; set; }

        public CasellaPosta Casella { get; set; } = default!;
        public ICollection<EmailAllegato> Allegati { get; set; } = new List<EmailAllegato>();
    }
}
