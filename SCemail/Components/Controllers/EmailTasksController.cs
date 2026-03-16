using Microsoft.AspNetCore.Mvc;
using SCemail.Components.Data;
using SCemail.Components.Shared;

namespace SCemail.Components.Controllers;

[ApiController]
[Route("api/email-tasks")]
public sealed class EmailTasksController : ControllerBase
{
    private readonly IEmailTasksRepository _repo;
    public EmailTasksController(IEmailTasksRepository repo) => _repo = repo;

    // -------------------------
    // LISTA TASK UTENTE
    // -------------------------
    [HttpGet("user/{username}")]
    public async Task<ActionResult<List<TaskDto>>> GetByUser(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return BadRequest("username mancante");

        var items = await _repo.GetForUserAsync(username);
        return Ok(items);
    }

    // -------------------------
    // DETTAGLIO TASK
    // -------------------------
    [HttpGet("{id:int}")]
    public async Task<ActionResult<TaskDto?>> GetById(int id)
        => Ok(await _repo.GetByIdAsync(id));

    // -------------------------
    // CREA TASK (da zero)
    // ✅ ritorna int (non {id})
    // -------------------------
    [HttpPost]
    public async Task<ActionResult<int>> Create([FromBody] CreateTaskRequest req)
    {
        if (req is null) return BadRequest();
        if (string.IsNullOrWhiteSpace(req.CreatoDa)) return BadRequest("CreatoDa mancante");
        if (string.IsNullOrWhiteSpace(req.Titolo)) return BadRequest("Titolo mancante");

        var id = await _repo.CreateAsync(req);
        return Ok(id);
    }

    // -------------------------
    // ASSEGNA / RIASSEGNA
    // -------------------------
    [HttpPut("{id:int}/assignees")]
    public async Task<IActionResult> SetAssignees(int id, [FromBody] SetAssigneesDto dto)
    {
        if (dto is null) return BadRequest();
        // Utente che fa l'azione (audit eventuale)
        if (string.IsNullOrWhiteSpace(dto.By)) return BadRequest("By mancante");

        await _repo.SetAssigneesAsync(id, dto.AssegnatiA ?? new());
        return NoContent();
    }

    // -------------------------
    // CHIUSURA TASK (completato)
    // -------------------------
    [HttpPut("{id:int}/close")]
    public async Task<IActionResult> Close(int id, [FromBody] CloseTaskRequest req)
    {
        if (req is null) return BadRequest();
        if (string.IsNullOrWhiteSpace(req.Utente)) return BadRequest("Utente mancante");

        await _repo.CloseAsync(id, req);
        return NoContent();
    }

    // -------------------------
    // HOME BADGES (home page)
    // -------------------------
    [HttpGet("/api/tasks/homeBadges")]
    public async Task<ActionResult<TaskHomeBadgesDto>> GetHomeBadges([FromQuery] string utente)
    {
        if (string.IsNullOrWhiteSpace(utente))
            return BadRequest("utente mancante");

        var dto = await _repo.GetHomeBadgesAsync(utente);
        return Ok(dto);
    }

    // -------------------------
    // MARK SEEN (quando apro il task)
    // -------------------------
    [HttpPut("{taskId:int}/inbox/seen")]
    public async Task<IActionResult> MarkSeen(int taskId, [FromQuery] string utente)
    {
        if (string.IsNullOrWhiteSpace(utente))
            return BadRequest("utente mancante");

        await _repo.MarkSeenAsync(taskId, utente);
        return NoContent();
    }

    // -------------------------
    // COMMENTI
    // -------------------------
    [HttpGet("{taskId:int}/comments")]
    public async Task<ActionResult<List<TaskCommentDto>>> GetComments(int taskId)
        => Ok(await _repo.GetCommentsAsync(taskId));

    [HttpPost("{taskId:int}/comments")]
    public async Task<ActionResult<int>> AddComment(int taskId, [FromBody] NewCommentDto dto)
    {
        if (dto is null) return BadRequest();
        if (string.IsNullOrWhiteSpace(dto.Utente)) return BadRequest("Utente mancante");
        if (string.IsNullOrWhiteSpace(dto.Testo)) return BadRequest("Testo mancante");

        var id = await _repo.AddCommentAsync(taskId, dto.Utente, dto.Testo, dto.ReplyTo);
        return Ok(id);
    }

    // =========================
    // DTO INPUT API
    // =========================
    public sealed record NewCommentDto(string Utente, string Testo, int? ReplyTo);

    public sealed record SetAssigneesDto(string By, List<string>? AssegnatiA);
}
