using System.Net.Http.Json;

namespace SCemail.Components.Shared;
public interface IEmailTasksClient
{
    Task<List<UserTaskItem>> GetForUserAsync(string username);
    Task<UserTaskItem?> GetByIdAsync(int id);
    Task UpdateCommentAsync(int id, string? comment);
    Task DeleteAsync(int id);
    Task CloseAsync(int id, string utente);
    Task<int> ForwardAsync(int emailId, string utente, string? commento, string? titolo);
    // commenti
    Task<List<TaskComment>> GetCommentsAsync(int taskId);
    Task<int> AddCommentAsync(int taskId, string utente, string testo, int? replyTo = null);
    Task ForwardToUserAsync(int taskId, string username, CancellationToken ct = default);
}

public sealed class EmailTasksClient : IEmailTasksClient
{
    private readonly HttpClient _http;
    public EmailTasksClient(HttpClient http) => _http = http;

    public Task<List<UserTaskItem>> GetForUserAsync(string username)
        => _http.GetFromJsonAsync<List<UserTaskItem>>($"api/email-tasks/{Uri.EscapeDataString(username)}")!;

    public Task<UserTaskItem?> GetByIdAsync(int id)
       => _http.GetFromJsonAsync<UserTaskItem?>($"api/email-tasks/by-id/{id}");
    public async Task UpdateCommentAsync(int id, string? comment)
    {
        var res = await _http.PutAsJsonAsync($"api/email-tasks/{id}/comment", new { comment });
        res.EnsureSuccessStatusCode();
    }

    public async Task DeleteAsync(int id)
    {
        var res = await _http.DeleteAsync($"api/email-tasks/{id}");
        res.EnsureSuccessStatusCode();
    }
    public async Task CloseAsync(int id, string utente)
    {
        var res = await _http.PutAsJsonAsync($"api/email-tasks/{id}/close", new { utente });
        res.EnsureSuccessStatusCode();
    }

    public async Task<int> ForwardAsync(int emailId, string utente, string? commento, string? titolo)
    {
        var res = await _http.PostAsJsonAsync("api/email-tasks/forward",
            new { EmailId = emailId, Utente = utente, Commento = commento, Titolo = titolo });
        res.EnsureSuccessStatusCode();
        var payload = await res.Content.ReadFromJsonAsync<Dictionary<string, int>>();
        return payload!["id"];
    }

    public async Task ForwardToUserAsync(int taskId, string username, CancellationToken ct = default)
    {
        var payload = new { TaskId = taskId, Username = username };
        var res = await _http.PostAsJsonAsync("api/email-tasks/forward-to-user", payload, ct);
        res.EnsureSuccessStatusCode();
    }

    // commenti
    public Task<List<TaskComment>> GetCommentsAsync(int taskId)
        => _http.GetFromJsonAsync<List<TaskComment>>($"api/email-tasks/{taskId}/comments")!;

    public async Task<int> AddCommentAsync(int taskId, string utente, string testo, int? replyTo = null)
    {
        var res = await _http.PostAsJsonAsync($"api/email-tasks/{taskId}/comments",
            new { Utente = utente, Testo = testo, ReplyTo = replyTo });
        res.EnsureSuccessStatusCode();
        var payload = await res.Content.ReadFromJsonAsync<Dictionary<string, int>>();
        return payload!["id"];
    }
}