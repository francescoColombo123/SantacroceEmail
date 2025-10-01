namespace SCemail.Components.Shared;
public sealed class UserTaskItem
{
    public int Id { get; set; }
    public int EmailId { get; set; }
    public string Utente { get; set; } = string.Empty;
    public string? Commento { get; set; }
    public DateTime DataCreazione { get; set; }
    public string? Titolo { get; set; }
    public string Stato { get; set; } = "APERTO";
    public DateTime? DataChiusura { get; set; }
}