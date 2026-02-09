using SCemail.Components.Shared;

namespace SCemail.Components.Data;

public interface IEmailTasksRepository
{
    Task<List<TaskDto>> GetForUserAsync(string username);
    Task<TaskDto?> GetByIdAsync(int id);

    Task<int> CreateAsync(CreateTaskRequest req);
    Task<int> CreateFromEmailAsync(int emailId, string creatoDa, string? titolo, string? descrizioneMarkdown, DateTime? dataScadenza, List<string> assegnatiA);

    Task SetAssigneesAsync(int taskId, List<string> assignees);

    Task CloseAsync(int taskId, CloseTaskRequest req);

    Task DeleteAsync(int id);

    Task<List<TaskCommentDto>> GetCommentsAsync(int taskId);
    Task<int> AddCommentAsync(int taskId, string utente, string testo, int? replyTo);
}
