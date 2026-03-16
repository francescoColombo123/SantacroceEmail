using System.Net.Http.Json;
using SCemail.Components.Data;   // <-- QUI stanno TaskHomeBadgesDto/TaskHomeItemDto

namespace SCemail.Components.Shared;

public interface IEmailTasksClient
{
    Task<List<TaskDto>?> GetForUserAsync(string username);
    Task<TaskDto?> GetByIdAsync(int id);

    Task DeleteAsync(int id);
    Task CloseAsync(int id, CloseTaskRequest body);

    Task<List<TaskCommentDto>> GetCommentsAsync(int taskId);
    Task<int> AddCommentAsync(int taskId, string utente, string testo, int? replyTo = null);
    Task<int> CreateAsync(CreateTaskRequest body);

    Task<TaskHomeBadgesDto?> GetHomeBadgesAsync(string utente);
    Task MarkTaskSeenAsync(int taskId, string utente);
}

public sealed class EmailTasksClient : IEmailTasksClient
{
    private readonly HttpClient _http;
    public EmailTasksClient(HttpClient http) => _http = http;

    public async Task<List<TaskDto>?> GetForUserAsync(string username)
    {
        var u = Uri.EscapeDataString(username);
        return await _http.GetFromJsonAsync<List<TaskDto>>($"api/email-tasks/user/{u}");
    }

    public Task<TaskDto?> GetByIdAsync(int id)
        => _http.GetFromJsonAsync<TaskDto?>($"api/email-tasks/{id}");

    public async Task<int> CreateAsync(CreateTaskRequest body)
    {
        var res = await _http.PostAsJsonAsync("api/email-tasks", body);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<int>();
    }

    public async Task DeleteAsync(int id)
    {
        var res = await _http.DeleteAsync($"api/email-tasks/{id}");
        res.EnsureSuccessStatusCode();
    }

    public async Task CloseAsync(int id, CloseTaskRequest body)
    {
        var res = await _http.PutAsJsonAsync($"api/email-tasks/{id}/close", body);
        res.EnsureSuccessStatusCode();
    }

    public Task<List<TaskCommentDto>> GetCommentsAsync(int taskId)
        => _http.GetFromJsonAsync<List<TaskCommentDto>>($"api/email-tasks/{taskId}/comments")!;

    public async Task<int> AddCommentAsync(int taskId, string utente, string testo, int? replyTo = null)
    {
        var res = await _http.PostAsJsonAsync(
            $"api/email-tasks/{taskId}/comments",
            new { Utente = utente, Testo = testo, ReplyTo = replyTo }
        );

        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<int>();
    }

    public async Task<TaskHomeBadgesDto?> GetHomeBadgesAsync(string utente)
    {
        var url = $"api/tasks/homeBadges?utente={Uri.EscapeDataString(utente)}";
        return await _http.GetFromJsonAsync<TaskHomeBadgesDto>(url);
    }

    public async Task MarkTaskSeenAsync(int taskId, string utente)
    {
        var url = $"api/email-tasks/{taskId}/inbox/seen?utente={Uri.EscapeDataString(utente)}";
        var res = await _http.PutAsync(url, null);
        res.EnsureSuccessStatusCode();
    }
}