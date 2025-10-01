using SCemail.Components.Shared;
using System.ComponentModel.DataAnnotations.Schema;


public class CasellaPosta
{
    public int Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string NomeCompleto { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string ImapHost { get; set; } = string.Empty;
    public int ImapPort { get; set; }
    public string UseSsl { get; set; } = "N";
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    // Firma
    [Column("NOME")]
    public string Nome { get; set; } = string.Empty;

    [Column("TITOLO")]
    public string Titolo { get; set; } = string.Empty;

    [Column("RECAPITO")]
    public string Recapito { get; set; } = string.Empty;

    public ICollection<EmailRicevuta> EmailRicevute { get; set; } = new List<EmailRicevuta>();
}