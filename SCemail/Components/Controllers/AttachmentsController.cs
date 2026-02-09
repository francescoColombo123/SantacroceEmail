using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SCemail; 
using SCemail.Components.Data;
using Microsoft.AspNetCore.StaticFiles;


namespace SCemail.Components.Controllers
{
    // Prefisso principale per allegati: /api/attachments
    [ApiController]
    [Route("api/attachments")]
    public class AttachmentsController : ControllerBase
    {
        private readonly MailService _mail;
        private readonly ILogger<AttachmentsController> _logger;
        private readonly IConfiguration _config;

        public record AttachmentMetaDto(int Id, string Filename, string Mime);
        private readonly IEmailAttachmentsRepository _attRepo;
        private readonly AttachmentsOptions _opt;
        private readonly FileExtensionContentTypeProvider _ct = new();
        private readonly EmailFetchService _fetch;

        private sealed record AllegatoEnsureInfo(
            int AllegatoId,
            int EmailId,
            int CasellaId,
            string FolderPath,
            long MessageUid,
            string FileName,
            string Mime,
            string PartSpec,
            string? FilePath
        );

        public AttachmentsController(
       MailService mail,
       IEmailAttachmentsRepository attRepo,
       IOptions<AttachmentsOptions> opt,
       ILogger<AttachmentsController> logger,
       IConfiguration config,
       EmailFetchService fetch)
        {
            _mail = mail;
            _attRepo = attRepo;
            _opt = opt.Value;
            _logger = logger;
            _config = config;
            _fetch = fetch;
        }



        // === ALLEGATI EMAIL ===

        [HttpGet("{id:int}/meta")]
        public async Task<ActionResult<AttachmentMetaDto>> Meta(int id, CancellationToken ct)
        {
            try
            {
                var (_, mime, filename) = await _mail.GetAttachmentAsync(id, ct);
                return new AttachmentMetaDto(id, filename, mime);
            }
            catch
            {
                return NotFound();
            }
        }
        [HttpGet("{id:int}/preview")]
        public Task<IActionResult> Preview(int id, CancellationToken ct)
       => ServeFromDisk(id, inline: true, previewOnly: true, ct);

        [HttpPost("{id:int}/ensure")]
        public async Task<IActionResult> Ensure(int id, CancellationToken ct)
        {
            var ok = await _fetch.EnsureSingleAttachmentAsync(id, ct);
            if (!ok) return NotFound(new { id, ensured = false });
            return Ok(new { id, ensured = true });
        }


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
                    var safeName = filename ?? "allegato";
                    Response.Headers["Content-Disposition"] =
                        $"inline; filename=\"{safeName}\"; filename*=UTF-8''{Uri.EscapeDataString(safeName)}";

                    return File(bytes, mime ?? "application/octet-stream", enableRangeProcessing: true);
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

                var msg = ex.Message ?? "";
                if (msg.Contains("Invalid credentials", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("Authentication", StringComparison.OrdinalIgnoreCase))
                {
                    return Unauthorized("Credenziali casella non aggiornate. Aggiorna la password della casella e riprova.");
                }

                return NotFound($"Allegato {id} non disponibile: {ex.Message}");
            }
        }

        private async Task<IActionResult> ServeFromDisk(int id, bool inline, bool previewOnly, CancellationToken ct)
        {
            try
            {
                var meta = await _attRepo.GetMetaAsync(id, ct);
                if (meta is null)
                    return NotFound($"Allegato {id} non presente a DB.");

                if (string.IsNullOrWhiteSpace(_opt.BasePath))
                    return Problem("Attachments:BasePath non configurato.");

                if (string.IsNullOrWhiteSpace(meta.FilePath))
                {
                    // prova a scaricarlo al volo tramite EmailFetchService
                    var ok = await _fetch.EnsureSingleAttachmentAsync(id, ct);
                    if (!ok) return NotFound($"Allegato {id} senza FILE_PATH e non recuperabile.");

                    // ricarico meta dopo ensure
                    meta = await _attRepo.GetMetaAsync(id, ct);
                    if (meta == null || string.IsNullOrWhiteSpace(meta.FilePath))
                        return NotFound($"Allegato {id} non recuperato (FILE_PATH ancora nullo).");
                }

                var fullPath = BuildSafeFullPath(_opt.BasePath, meta.FilePath);
                if (fullPath is null)
                    return BadRequest("FILE_PATH non valido (path traversal).");

                if (!System.IO.File.Exists(fullPath))
                {
                    // se DB dice file ma non esiste, riprovo 1 volta a ricrearlo
                    var ok = await _fetch.EnsureSingleAttachmentAsync(id, ct);
                    if (!ok) return NotFound($"Allegato {id} non trovato su disco e non recuperabile.");

                    meta = await _attRepo.GetMetaAsync(id, ct);
                    if (meta == null || string.IsNullOrWhiteSpace(meta.FilePath))
                        return NotFound($"Allegato {id} non recuperato.");

                    fullPath = BuildSafeFullPath(_opt.BasePath, meta.FilePath);
                    if (fullPath is null || !System.IO.File.Exists(fullPath))
                        return NotFound($"Allegato {id} non trovato su disco dopo ensure.");
                }

                // MIME: preferisci DB, altrimenti da estensione
                var mime = (meta.MimeType ?? "").Trim();
                if (string.IsNullOrWhiteSpace(mime))
                {
                    if (!_ct.TryGetContentType(fullPath, out var guessed))
                        guessed = "application/octet-stream";
                    mime = guessed;
                }

                if (previewOnly && !IsPreviewable(mime))
                    return StatusCode(StatusCodes.Status415UnsupportedMediaType,
                        "Anteprima disponibile solo per PDF e immagini.");

                Response.Headers["X-Content-Type-Options"] = "nosniff";

                var safeName = string.IsNullOrWhiteSpace(meta.NomeFile) ? "allegato" : meta.NomeFile;
                var dispo = inline ? "inline" : "attachment";
                Response.Headers["Content-Disposition"] =
                    $"{dispo}; filename=\"{safeName}\"; filename*=UTF-8''{Uri.EscapeDataString(safeName)}";

                var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                return File(stream, mime, enableRangeProcessing: true);
            }
            catch (OperationCanceledException)
            {
                return StatusCode(StatusCodes.Status499ClientClosedRequest);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Errore ServeFromDisk allegato Id={Id}", id);
                return NotFound($"Allegato {id} non disponibile: {ex.Message}");
            }
        }

        private static bool IsPreviewable(string mime)
            => mime.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)
               || mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

        private static string? BuildSafeFullPath(string basePath, string relativePathFromDb)
        {
            // Normalizza separatori
            var rel = (relativePathFromDb ?? "")
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);

            // Combina e normalizza
            var combined = Path.Combine(basePath, rel);
            var full = Path.GetFullPath(combined);

            var baseFull = Path.GetFullPath(basePath);
            if (!baseFull.EndsWith(Path.DirectorySeparatorChar))
                baseFull += Path.DirectorySeparatorChar;

            return full.StartsWith(baseFull, StringComparison.OrdinalIgnoreCase) ? full : null;
        }



    }
}
