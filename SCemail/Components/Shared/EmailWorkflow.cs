using System.ComponentModel.DataAnnotations.Schema;

namespace SCemail.Components.Shared
{
    [Table("EMAIL_WORKFLOW", Schema = "SGAPP")]
    public class EmailWorkflow
    {
        public int Id { get; set; }

        [Column("EMAIL_ID")]
        public int EmailId { get; set; }

        [Column("ASSEGNATO_A")]
        public string AssegnatoA { get; set; } = null!;

        [Column("STATO")]
        public string Stato { get; set; } = "in_lavorazione";

        [Column("DATA_ASSEGNAZIONE")]
        public DateTime DataAssegnazione { get; set; } = DateTime.Now;

        [Column("DATA_COMPLETAMENTO")]
        public DateTime? DataCompletamento { get; set; }

        [Column("NOTE")]
        public string? Note { get; set; }
    }
}
