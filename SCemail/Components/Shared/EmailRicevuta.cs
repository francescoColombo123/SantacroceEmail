using System.ComponentModel.DataAnnotations.Schema;

namespace SCemail.Components.Data
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
        [Column("APERTO")]
        public string? Aperto { get; set; }
        [Column("ELIMINATO")]
        public string? Eliminato { get; set; }

        // NUOVI
        public string? FolderPath { get; set; }
        public long? MessageUid { get; set; }
        public string? InReplyTo { get; set; }
        [Column("REFERENCES_HDR")]
        public string? ReferencesHdr { get; set; }
        public CasellaPosta Casella { get; set; } = default!;
        public string? ThreadKey { get; set; }
        public ICollection<EmailAllegato> Allegati { get; set; } = new List<EmailAllegato>();
    }
}
