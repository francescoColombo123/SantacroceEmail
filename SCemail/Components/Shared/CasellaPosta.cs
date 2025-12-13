using SCemail.Components.Data;
using SCemail.Components.Data;
using System.ComponentModel.DataAnnotations.Schema;


public class CasellaPosta
{
    public int Id { get; set; }
    public string Email { get; set; } = null!;
    public string Password { get; set; } = null!;
    public string Provider { get; set; } = null!;
    public string ImapHost { get; set; } = null!;
    public int ImapPort { get; set; }
    public string UseSsl { get; set; } = "Y";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    [Column("NOME_BK")]
    public string? NomeBk { get; set; }

    [Column("TITOLO_BK")]
    public string? TitoloBk { get; set; }

    [Column("RECAPITO_BK")]
    public string? RecapitoBk { get; set; }

    public ICollection<EmailRicevuta> EmailRicevute { get; set; } = new List<EmailRicevuta>();
    public ICollection<CasellaAbilitazione> Abilitazioni { get; set; } = new List<CasellaAbilitazione>();

}