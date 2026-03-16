using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SCemail.Components.Data;

[Table("EMAIL_INBOX_SEZIONE_MAP", Schema = "SGAPP")]
public class EmailInboxSezioneMap
{
    [Key]
    [Column("ID")]
    public long Id { get; set; }

    [Required]
    [Column("ID_EMAIL")]
    public long IdEmail { get; set; }

    [Required]
    [Column("ID_SEZIONE")]
    public long IdSezione { get; set; }

    [Required]
    [Column("UTENTE")]
    [MaxLength(100)]
    public string Utente { get; set; } = "";

    [Required]
    [Column("UPDATED_AT")]
    public DateTime UpdatedAt { get; set; }

    [Column("UPDATED_BY")]
    [MaxLength(100)]
    public string? UpdatedBy { get; set; }

    // (opzionale) navigation
    public EmailInboxSezione? Sezione { get; set; }
}
