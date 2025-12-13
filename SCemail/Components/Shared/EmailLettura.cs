using SCemail.Components.Data;

namespace SCemail.Components.Shared
{
    public class EmailLetturaUtente
    {
        public int Id { get; set; }
        public int EmailId { get; set; }
        public string Utente { get; set; } = null!;
        public DateTime DataLettura { get; set; }

        public EmailRicevuta Email { get; set; } = null!;
    }
}
