using System.ComponentModel.DataAnnotations.Schema;

namespace SCemail.Components.Data
{
    public class CasellaPosta
    {
        public int Id { get; set; }
        public string Email { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string Provider { get; set; } = string.Empty;
        public string ImapHost { get; set; } = string.Empty;
        public int ImapPort { get; set; }
        public string UseSsl { get; set; } = "Y";
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public string? NomeBk { get; set; }
        public string? TitoloBk { get; set; }
        public string? RecapitoBk { get; set; }
        [NotMapped]
        public bool InTest { get; set; } = false;

        [Column("STATO_CONN")]
        public string? StatoConn { get; set; } // OK | ERRORE

        public ICollection<CasellaAbilitazione> Abilitazioni { get; set; } = new List<CasellaAbilitazione>();
        public ICollection<EmailRicevuta> EmailRicevute { get; set; } = new List<EmailRicevuta>();
        public ICollection<EmailInviata> EmailInviate { get; set; } = new List<EmailInviata>();
        [NotMapped]
        public StatoConnessione Stato { get; set; } = StatoConnessione.Sconosciuto;

        [NotMapped]
        public string? LastError { get; set; }

    }

    public enum StatoConnessione
    {
        Sconosciuto,
        Ok,
        Errore
    }
}
