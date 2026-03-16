using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SCemail.Components.Data;

[Table("EMAIL_INBOX_SEZIONI", Schema = "SGAPP") ]
public class EmailInboxSezione
{
    [Key]
    [Column("ID")]
    public long Id { get; set; }

    [Required]
    [Column("CODICE")]
    [MaxLength(30)]
    public string Codice { get; set; } = "";

    [Required]
    [Column("NOME")]
    [MaxLength(100)]
    public string Nome { get; set; } = "";

    [Required]
    [Column("ORDINE")]
    public long Ordine { get; set; }

    [Required]
    [Column("ATTIVA")]
    [MaxLength(1)]
    public string Attiva { get; set; } = "S"; // 'S' / 'N'
}
