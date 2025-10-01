using System;
using System.Collections.Generic;

namespace SCemail.Components.Data
{
    /* ======== MAIL (NEW) ======== */
    public record AllegatoItem_NEW(int Id, string NomeFile, string? MimeType);

    public record EmailListItem_NEW(
        int Id,
        DateTime? Data,
        string? Mittente,
        string? Oggetto,
        string? Aperto,
        bool HasAttachments = false,
        int Replies = 0,
        string? Preview = null,
        List<AllegatoItem_NEW>? Allegati = null
    );

    public class EmailDetail_NEW
    {
        public int Id { get; set; }
        public string? Mittente { get; set; }
        public string? Destinatari { get; set; }
        public string? Oggetto { get; set; }
        public DateTime? Data { get; set; }
        public string? CorpoHtml { get; set; }
        public string? CorpoTesto { get; set; }
        public IEnumerable<AllegatoItem_NEW>? Allegati { get; set; }
        public string? Aperto { get; set; }
        public bool IsLoaded { get; set; }   // per lazy-load
    }



    public record FolderItem_NEW(string UiName, string Label, string IconClass, int SortOrder, int Count);

    public record OutgoingAttachment_NEW(string FileName, string? MimeType, byte[] Content);

    /* ======== TASK (NEW) ======== */
    public record UserTaskItem_NEW(
        int Id,
        string Utente,
        string EmailId,
        string Titolo,
        string? Commento,
        string Stato,
        DateTime DataCreazione
    );

    public record TaskComment_NEW(
        int Id,
        int TaskId,
        string Utente,
        string Testo,
        DateTime DataCreazione
    );
}
