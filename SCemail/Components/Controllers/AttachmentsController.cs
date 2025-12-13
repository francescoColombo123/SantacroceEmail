using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SCemail.Components.Data;

namespace SCemail.Components.Controllers
{
    // Prefisso principale per allegati: /api/attachments
    [ApiController]
    [Route("api/attachments")]
    public class AttachmentsController : ControllerBase
    {
        private readonly MailService _mail;
        private readonly ILogger<AttachmentsController> _logger;

        public AttachmentsController(MailService mail, ILogger<AttachmentsController> logger)
        {
            _mail = mail;
            _logger = logger;
        }

        // === ALLEGATI EMAIL ===

        // Anteprima inline -> /api/attachments/{id}/inline
        [HttpGet("{id:int}/inline")]
        public async Task<IActionResult> Inline(int id, CancellationToken ct)
            => await GetFile(id, inline: true, ct);

        // Download compatibile -> /api/attachments/{id}
        [HttpGet("{id:int}")]
        public async Task<IActionResult> DownloadCompat(int id, CancellationToken ct)
            => await GetFile(id, inline: false, ct);

        // Download esplicito -> /api/attachments/{id}/download
        [HttpGet("{id:int}/download")]
        public async Task<IActionResult> Download(int id, CancellationToken ct)
            => await GetFile(id, inline: false, ct);

        // === ALLEGATI COMMENTI ===
        // Route ASSOLUTA per commenti (non soggetta al prefisso /api/attachments)
        [HttpGet("/api/comments/{id:int}/attachment")]
        public async Task<IActionResult> GetCommentAttachment(int id, CancellationToken ct)
        {
            var res = await _mail.GetCommentAttachmentAsync(id, ct);
            if (res is null)
                return NotFound();

            return File(res.Value.data, "application/octet-stream", res.Value.filename);
        }

        // === LOGICA COMUNE ===
        private async Task<IActionResult> GetFile(int id, bool inline, CancellationToken ct)
        {
            try
            {
                var (bytes, mime, filename) = await _mail.GetAttachmentAsync(id, ct);

                if (inline)
                {
                    Response.Headers["Content-Disposition"] = $"inline; filename=\"{filename}\"";
                    return File(bytes, mime);
                }

                return File(bytes, mime, fileDownloadName: filename, enableRangeProcessing: true);
            }
            catch (OperationCanceledException)
            {
                return StatusCode(StatusCodes.Status499ClientClosedRequest);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Errore nel recupero allegato Id={Id}", id);
                return NotFound($"Allegato {id} non disponibile: {ex.Message}");
            }
        }
    }
}
