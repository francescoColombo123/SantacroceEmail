namespace SCemail.Components.Data
{
    public class TaskParticipant
    {
        public int Id { get; set; }
        public int TaskId { get; set; }
        public string Utente { get; set; } = "";
        public DateTime DataCreazione { get; set; } = DateTime.UtcNow;
    }
}
