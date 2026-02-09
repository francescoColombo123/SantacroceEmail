namespace SCemail.Components.Shared
{
    public record TaskDto(
        int Id,
        int? EmailId,
        string Titolo,
        string? DescrizioneMarkdown,
        string CreatoDa,
        DateTime DataCreazione,
        DateTime? DataScadenza,
        string Stato,
        DateTime? DataChiusura,
        string? ChiusoDa,
        List<string> AssegnatiA
    );
    public static class TaskStati
    {
        public const string Aperto = "APERTO";
        public const string Chiuso = "CHIUSO";
    }

    public record CreateTaskRequest(
        string CreatoDa,
        string Titolo,
        string? DescrizioneMarkdown,
        DateTime? DataScadenza,
        List<string> AssegnatiA,
        int? EmailId = null
    );

    public record CloseTaskRequest(
        string Utente,
        string? Commento
    );

    public record TaskCommentDto(
        int Id,
        int TaskId,
        string Utente,
        string Testo,
        DateTime DataCreazione,
        int? ReplyTo
    );
}
