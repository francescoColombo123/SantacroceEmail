namespace SCemail.Components.Data;

public sealed class TaskHomeBadgesDto
{
    public int UnreadTotal { get; set; }
    public int ToCloseCount { get; set; }
    public int WaitingReviewCount { get; set; }
    public int AssignedUnreadTotal { get; set; }
    public List<TaskHomeItemDto> AssignedTop { get; set; } = new();
    public List<TaskHomeItemDto> UnreadTop { get; set; } = new();
    public List<TaskHomeItemDto> ToCloseTop { get; set; } = new();
    public List<TaskHomeItemDto> WaitingReviewTop { get; set; } = new();
}

public sealed class TaskHomeItemDto
{
    public int TaskId { get; set; }
    public string Titolo { get; set; } = "";

    public DateTime DataCreazione { get; set; }
    public DateTime? DataScadenza { get; set; }
    public string CreatoDa { get; set; } = "";

    // ✅ aggiunte per compat con repository
    public int UnreadCount { get; set; }
    public string LastEventText { get; set; } = "";
    public DateTime? LastEventAt { get; set; } // utile, anche se non la usi ora
}