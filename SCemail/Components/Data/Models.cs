using System;
using System.Collections.Generic;

namespace SCemail.Components.Data
{

    public record FolderItem(string UiName, string Label, string IconClass, int SortOrder, int Count);

    public record OutgoingAttachment(string FileName, string? MimeType, byte[] Content);




    // ============ TASKS ============
    public class UserTaskItem
    {
        public int Id { get; set; }
        public int EmailId { get; set; }
        public string Titolo { get; set; } = "";
        public string Stato { get; set; } = "APERTO"; // APERTO | IN_LAVORAZIONE | CHIUSO
        public string? Commento { get; set; }
        public DateTime DataCreazione { get; set; } = DateTime.Now;
    }

    public class TaskComment
    {
        public int Id { get; set; }
        public int TaskId { get; set; }
        public string Utente { get; set; } = "";
        public string Testo { get; set; } = "";
        public DateTime DataCreazione { get; set; } = DateTime.Now;
    }
   
}
