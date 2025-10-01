namespace SCemail.Components.Shared;
public sealed class TaskComment
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public string Utente { get; set; } = string.Empty;
    public string Testo { get; set; } = string.Empty;
    public DateTime DataCreazione { get; set; }
    public int? ReplyTo { get; set; }
}