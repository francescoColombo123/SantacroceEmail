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

        [Column("CODICE_CLIENTE")]
        public long? CodiceCliente { get; set; }

        [Column("TITOLO")]
        [MaxLength(20)]
        public string? Titolo { get; set; } = "";

        [Column("FIRST_NAME")]
        [MaxLength(100)]
        public string? FirstName { get; set; }

        [Column("MIDDLE_NAME")]
        [MaxLength(100)]
        public string? MiddleName { get; set; }

        [Column("LAST_NAME")]
        [MaxLength(100)]
        public string? LastName { get; set; }

        [Column("AZIENDA")]
        [MaxLength(255)]
        public string? Azienda { get; set; }

        [Column("EMAIL")]
        [MaxLength(255)]
        public string? Email { get; set; }

        [Column("TELEFONO_UFFICIO")]
        [MaxLength(50)]
        public string? TelefonoUfficio { get; set; }

        [Column("TELEFONO_CELLULARE")]
        [MaxLength(50)]
        public string? TelefonoCellulare { get; set; }

        [Column("TELEFONO_PRIVATO")]
        [MaxLength(50)]
        public string? TelefonoPrivato { get; set; }

        [Column("CREATED_AT")]
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}