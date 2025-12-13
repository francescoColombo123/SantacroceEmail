using System.ComponentModel.DataAnnotations.Schema;

namespace SCemail.Components.Data
{
    [Table("CASELLA_ABILITAZIONI", Schema = "SGAPP")]
    public class CasellaAbilitazione
    {
        [Column("ID")]
        public int Id { get; set; }

        [Column("CASELLA_ID")]
        public int CasellaId { get; set; }

        [Column("USERNAME")]
        public string Username { get; set; } = string.Empty;

        [Column("NOME")]
        public string Nome { get; set; } = string.Empty;

        [Column("TITOLO")]
        public string? Titolo { get; set; }

        [Column("RECAPITO")]
        public string? Recapito { get; set; }

        [Column("CREATED_AT")]
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        [Column("IS_ADMIN")]
        public bool IsAdmin { get; set; }   // 👈 NUOVO!


        // ✅ chiave esterna esplicita (ORA-00904 fix)
        [ForeignKey(nameof(CasellaId))]
        public virtual CasellaPosta Casella { get; set; } = null!;
    }
}
