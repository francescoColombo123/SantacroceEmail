using System.Net.Http.Json;
using SCemail.Components.Data;

namespace SCemail.Components.Shared
{
    public interface IEmailTasksClient_NEW
    {
        Task<List<UserTaskItem_NEW>> GetForUserAsync(string utente);
        Task<List<TaskComment_NEW>> GetCommentsAsync(int taskId);
        Task<int> AddCommentAsync(int taskId, string utente, string testo);
        Task CloseAsync(int taskId, string utente);
    }

    // Implementazione REST molto semplice: adegua le URL alle tue API reali
    public class EmailTasksClient_NEW : IEmailTasksClient_NEW
    {
        private readonly HttpClient _http;
        private readonly ILogger<EmailTasksClient_NEW> _logger;
        private readonly string _base = "api/email-tasks";

        public EmailTasksClient_NEW(HttpClient http, ILogger<EmailTasksClient_NEW> logger)
        {
            _http = http;
            _logger = logger;
        }

        public async Task<List<UserTaskItem_NEW>> GetForUserAsync(string utente)
        {
            try
            {
                var res = await _http.GetFromJsonAsync<List<UserTaskItem_NEW>>($"{_base}/for-user/{Uri.EscapeDataString(utente)}");
                return res ?? new List<UserTaskItem_NEW>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Errore GetForUserAsync");
                return new List<UserTaskItem_NEW>();
            }
        }

        public async Task<List<TaskComment_NEW>> GetCommentsAsync(int taskId)
        {
            try
            {
                var res = await _http.GetFromJsonAsync<List<TaskComment_NEW>>($"{_base}/{taskId}/comments");
                return res ?? new List<TaskComment_NEW>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Errore GetCommentsAsync");
                return new List<TaskComment_NEW>();
            }
        }

        public async Task<int> AddCommentAsync(int taskId, string utente, string testo)
        {
            var dto = new { utente, testo };
            var res = await _http.PostAsJsonAsync($"{_base}/{taskId}/comments", dto);
            res.EnsureSuccessStatusCode();
            var id = await res.Content.ReadFromJsonAsync<int>();
            return id;
        }

        public async Task CloseAsync(int taskId, string utente)
        {
            var dto = new { utente };
            var res = await _http.PostAsJsonAsync($"{_base}/{taskId}/close", dto);
            res.EnsureSuccessStatusCode();
        }
    }
}
