using System;
using System.ComponentModel.DataAnnotations.Schema;
using static SCemail.Components.Data.MailService_NEW;

namespace SCemail.Components.Data
{
    [Table("EMAIL_MENZIONI", Schema = "SGAPP")]
    public class EmailMenzione
    {
        [Column("ID")]
        public int Id { get; set; }

        [Column("EMAIL_ID")]
        public int EmailId { get; set; }

        [Column("UTENTE")]
        public string? Utente { get; set; } = string.Empty;

        [Column("TIPO_EVENTO")]
        public string? TipoEvento { get; set; } = string.Empty;
        [Column("DATA_MENZIONE")]
        public DateTime DataMenzione { get; set; }

        [Column("COMMENTO_ID")]
        public int? CommentoId { get; set; }
        [Column("VISTO")]
        public string? Visto { get; set; } = "N";

        // 🔗 FK verso EMAIL_RICEVUTE
        [ForeignKey(nameof(EmailId))]
        public virtual EmailRicevuta Email { get; set; } = null!;

        // 🔗 FK verso EMAIL_COMMENTI (opzionale)
        [ForeignKey(nameof(CommentoId))]
        public virtual EmailTask? Commento { get; set; }
    }
}
