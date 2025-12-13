using SCemail.Components.Data;

namespace SCemail.Components.Pages
{
    public class EmailAssegnazione
    {
        public int Id { get; set; }               
        public int EmailId { get; set; }        
        public string Utente { get; set; } = null!;
        public string SoloInvio { get; set; } = "N"; 

        public EmailRicevuta? Email { get; set; }
    }
}
