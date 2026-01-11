using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SCemail.Components.Data
{
    [Table("RUBRICA_CONTATTI", Schema = "SGAPP")]
    public class RubricaContatto
    {
        [Key]
        [Column("ID")]
        public int Id { get; set; }

        [Column("FIRST_NAME")]
        [MaxLength(100)]
        public string? FirstName { get; set; }

        [Column("MIDDLE_NAME")]
        [MaxLength(100)]
        public string? MiddleName { get; set; }

        [Column("LAST_NAME")]
        [MaxLength(100)]
        public string? LastName { get; set; }

        [Column("EMAIL")]
        [Required]
        [MaxLength(255)]
        public string Email { get; set; } = string.Empty;

        [Column("CREATED_AT")]
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}
