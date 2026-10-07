using SCemail.Components.Data;
using System.ComponentModel.DataAnnotations.Schema;

namespace SCemail.Components.Pages
{
    public class EmailAssegnazione
    {
        public int Id { get; set; }               
        public int EmailId { get; set; }        
        public string Utente { get; set; } = null!;
        public string SoloInvio { get; set; } = "N";
        [Column("DATA_ASSEGNAZIONE")]
        public DateTime? DataAssegnazione { get; set; }

        public EmailRicevuta? Email { get; set; }
    }
}
