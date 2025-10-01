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

    [HttpGet("{username}")]
    public async Task<ActionResult<IEnumerable<Data.UserTaskItem>>> GetByUser(string username)
        => Ok(await _repo.GetForUserAsync(username));

    [HttpGet("by-id/{id:int}")]
    public async Task<ActionResult<Data.UserTaskItem?>> GetById(int id)
        => Ok(await _repo.GetByIdAsync(id));

    [HttpPost("forward")]
    public async Task<IActionResult> Forward([FromBody] ForwardDto dto)
    {
        if (dto is null || string.IsNullOrWhiteSpace(dto.Utente)) return BadRequest();
        var id = await _repo.CreateFromEmailAsync(dto.EmailId, dto.Utente, dto.Commento, dto.Titolo);
        return Ok(new { id });
    }

    [HttpPut("{id:int}/close")]
    public async Task<IActionResult> Close(int id, [FromBody] CloseDto body)
    {
        await _repo.CloseAsync(id, body.Utente);
        return NoContent();
    }

    [HttpPut("{id:int}/comment")]
    public async Task<IActionResult> UpdateComment(int id, [FromBody] CommentDto body)
    {
        await _repo.UpdateCommentAsync(id, body.Comment);
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        await _repo.DeleteAsync(id);
        return NoContent();
    }

    // ----- Commenti -----
    [HttpGet("{taskId:int}/comments")]
    public async Task<ActionResult<IEnumerable<Data.TaskComment>>> GetComments(int taskId)
        => Ok(await _repo.GetCommentsAsync(taskId));

    [HttpPost("{taskId:int}/comments")]
    public async Task<ActionResult<object>> AddComment(int taskId, [FromBody] NewCommentDto dto)
    {
        var id = await _repo.AddCommentAsync(taskId, dto.Utente, dto.Testo, dto.ReplyTo);
        return Ok(new { id });
    }
    public sealed record ForwardDto(int EmailId, string Utente, string? Commento, string? Titolo);
    public sealed record CommentDto(string? Comment);
    public sealed record NewCommentDto(string Utente, string Testo, int? ReplyTo);
    public sealed record CloseDto(string Utente);
}