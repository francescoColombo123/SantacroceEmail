using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SCemail;
using SCemail.Components.Data;


namespace SCemail.Components.Controllers
{
    // Prefisso principale per allegati: /api/attachments
    [ApiController]
    [Route("api/attachments")]
    public class AttachmentsController : ControllerBase
    {
        private readonly MailService _mail;
        private readonly MailService_NEW mailService_NEW;
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
        private sealed record EmlSavedPart(
    string FullPath,
    string RelativePath,
    string FileName,
    string Mime
);

        public record EmlViewDto(
    string? Oggetto,
    string? Mittente,
    string? Destinatari,
    string? Cc,
    DateTime? Data,
    string? CorpoHtml,
    string? CorpoTesto,
    List<EmlPartDto> Allegati
);

        public record EmlPartDto(
            int Index,
            string NomeFile,
            string MimeType,
            long Size,
            bool IsEmailEml
        );
        private sealed record EmlAttachmentView(
                int Index,
                string FileName,
                string Mime,
                long Size
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

        public record AttachmentListItemDto(int Id, string Filename, string Mime);

        public record AttachmentViewerContextDto(
            int CurrentId,
            List<AttachmentListItemDto> Attachments
        );

        private static List<int> ParseIds(string? idsRaw)
        {
            if (string.IsNullOrWhiteSpace(idsRaw))
                return new List<int>();

            return idsRaw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => int.TryParse(x, out var id) ? id : 0)
                .Where(id => id > 0)
                .Distinct()
                .ToList();
        }

        private static List<AttachmentListItemDto> AlignContextAttachments(
            List<AttachmentListItemDto> attachments,
            int currentId,
            List<int> requestedIds)
        {
            var unique = attachments
                .GroupBy(x => x.Id)
                .Select(group => group.First())
                .ToList();

            if (requestedIds.Count == 0)
                return unique;

            var orderMap = requestedIds
                .Select((id, idx) => new { id, idx })
                .GroupBy(x => x.id)
                .ToDictionary(g => g.Key, g => g.First().idx);

            var aligned = unique
                .Where(x => orderMap.ContainsKey(x.Id))
                .OrderBy(x => orderMap[x.Id])
                .ToList();

            if (!aligned.Any(x => x.Id == currentId))
            {
                var clicked = unique.FirstOrDefault(x => x.Id == currentId);
                if (clicked is not null)
                    aligned.Insert(0, clicked);
            }

            return aligned.Count > 0 ? aligned : unique;
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
        public Task<IActionResult> Preview(int id, [FromQuery] string? src, CancellationToken ct)
    => ServeFromDisk(id, src, inline: true, previewOnly: true, ct);

        [HttpPost("{id:int}/ensure")]
        public async Task<IActionResult> Ensure(int id, [FromQuery] string? src, CancellationToken ct)
        {
            var ok = await _fetch.EnsureSingleAttachmentAsync(id, ct);
            if (!ok) return NotFound(new { id, ensured = false, src });
            return Ok(new { id, ensured = true, src });
        }


        // Anteprima inline -> /api/attachments/{id}/inline
        [HttpGet("{id:int}/inline")]
        public Task<IActionResult> Inline(int id, [FromQuery] string? src, CancellationToken ct)
     => ServeFromDisk(id, src, inline: true, previewOnly: false, ct);

        // Download compatibile -> /api/attachments/{id}
        [HttpGet("{id:int}")]
        public async Task<IActionResult> DownloadCompat(int id, CancellationToken ct)
            => await GetFile(id, inline: false, ct);

        // Download esplicito -> /api/attachments/{id}/download
        [HttpGet("{id:int}/download")]
        public Task<IActionResult> Download(int id, [FromQuery] string? src, CancellationToken ct)
     => ServeFromDisk(id, src, inline: false, previewOnly: false, ct);

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

        [HttpGet("{id:int}/previewInternal")]
        public Task<IActionResult> PreviewInternal(int id, CancellationToken ct)
    => ServeSentFromDisk(id, inline: true, previewOnly: true, ct);

        [HttpGet("{id:int}/downloadInternal")]
        public Task<IActionResult> DownloadInternal(int id, CancellationToken ct)
            => ServeSentFromDisk(id, inline: false, previewOnly: false, ct);
        [HttpGet("{id:int}/open-eml-view")]
        public async Task<IActionResult> OpenEmlView(int id, CancellationToken ct)
        {
            var fileBytes = await GetAttachmentBytes(id, ct);

            if (fileBytes == null || fileBytes.Length == 0)
                return NotFound($"EML allegato {id} non trovato.");

            using var ms = new MemoryStream(fileBytes);
            var msg = await MimeKit.MimeMessage.LoadAsync(ms, ct);

            var html = BuildEmlHtml(msg, id);

            return Content(html, "text/html; charset=utf-8");
        }

        [HttpGet("{id:int}/eml-view-data")]
        public async Task<ActionResult<EmlViewDto>> GetEmlViewData(int id, CancellationToken ct)
        {
            var fileBytes = await GetAttachmentBytes(id, ct);

            if (fileBytes == null || fileBytes.Length == 0)
                return NotFound();

            using var ms = new MemoryStream(fileBytes);
            var msg = await MimeKit.MimeMessage.LoadAsync(ms, ct);

            var body = RenderMimeEntity(msg.Body);
            body = StripActiveContent(body);

            var attachments = GetAllAttachments(msg.Body);

            // salva subito tutti gli allegati interni nel path eml-preview
            foreach (var a in attachments)
                await EnsureEmlPartSavedAsync(id, a.Index, ct);

            var dto = new EmlViewDto(
                Oggetto: msg.Subject,
                Mittente: msg.From?.ToString(),
                Destinatari: msg.To?.ToString(),
                Cc: msg.Cc?.ToString(),
                Data: msg.Date.LocalDateTime,
                CorpoHtml: body,
                CorpoTesto: null,
                Allegati: attachments.Select(a => new EmlPartDto(
                    a.Index,
                    a.FileName,
                    ResolveMimeForEmlPart(a.FileName, a.Mime),
                    a.Size,
                    string.Equals(a.Mime, "message/rfc822", StringComparison.OrdinalIgnoreCase)
                        || a.FileName.EndsWith(".eml", StringComparison.OrdinalIgnoreCase)
                )).ToList()
            );

            return Ok(dto);
        }

        [HttpGet("{emlId:int}/eml-part/{index:int}/viewer")]
        public async Task<IActionResult> ViewEmlPart(int emlId, int index, CancellationToken ct)
        {
            var saved = await EnsureEmlPartSavedAsync(emlId, index, ct);

            if (saved == null)
                return NotFound();

            var src = $"/api/attachments/{emlId}/eml-part/{index}/inline";

            var html = $@"
            <!doctype html>
            <html>
            <head>
            <meta charset='utf-8'>
            <title>{System.Net.WebUtility.HtmlEncode(saved.FileName)}</title>
            <style>
            html, body {{
                margin: 0;
                width: 100%;
                height: 100%;
                background: #111827;
            }}
            iframe {{
                width: 100%;
                height: 100vh;
                border: 0;
                background: white;
            }}
            </style>
            </head>
            <body>
            <iframe src='{src}'></iframe>
            </body>
            </html>";

            return Content(html, "text/html; charset=utf-8");
        }

        [HttpGet("{emlId:int}/eml-part/{index:int}/inline")]
        public async Task<IActionResult> OpenEmlPartInline(int emlId, int index, CancellationToken ct)
        {
            return await ServeEmlPart(emlId, index, inline: true, ct);
        }

        [HttpGet("{emlId:int}/eml-part/{index:int}/download")]
        public async Task<IActionResult> DownloadEmlPart(int emlId, int index, CancellationToken ct)
        {
            return await ServeEmlPart(emlId, index, inline: false, ct);
        }
        [HttpGet("{id:int}/context")]
        public async Task<ActionResult<AttachmentViewerContextDto>> GetViewerContext(
    int id,
    [FromQuery] string? src,
    [FromQuery] string? ids,
    CancellationToken ct)
        {
            try
            {
                var requestedIds = ParseIds(ids);

                if (string.Equals(src, "sent", StringComparison.OrdinalIgnoreCase))
                {
                    var items = await _attRepo.GetSentAttachmentsOfSameEmailAsync(id, ct);
                    if (items is null || items.Count == 0)
                        return NotFound();

                    if (!items.Any(x => x.Id == id))
                        return NotFound();

                    var mapped = items.Select(x => new AttachmentListItemDto(
                            x.Id,
                            x.NomeFile ?? "allegato",
                            string.IsNullOrWhiteSpace(x.MimeType) ? "application/octet-stream" : x.MimeType
                        )).ToList();

                    var result = new AttachmentViewerContextDto(
                        id,
                        AlignContextAttachments(mapped, id, requestedIds)
                    );

                    return Ok(result);
                }
                else
                {
                    var items = await _attRepo.GetAttachmentsOfSameEmailAsync(id, ct);
                    if (items is null || items.Count == 0)
                        return NotFound();

                    if (!items.Any(x => x.Id == id))
                        return NotFound();

                    var mapped = items.Select(x => new AttachmentListItemDto(
                            (int)x.Id,
                            x.NomeFile ?? "allegato",
                            string.IsNullOrWhiteSpace(x.MimeType) ? "application/octet-stream" : x.MimeType
                        )).ToList();

                    var result = new AttachmentViewerContextDto(
                        id,
                        AlignContextAttachments(mapped, id, requestedIds)
                    );

                    return Ok(result);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Errore nel recupero context allegato Id={Id}", id);
                return NotFound();
            }
        }

        [HttpGet("{id:int}/debug-meta")]
        public async Task<IActionResult> DebugMeta(int id, CancellationToken ct)
        {
            var meta = await _attRepo.GetSentAttachmentMetaAsync(id, ct);
            if (meta is null) return NotFound();

            var fullPath = string.IsNullOrWhiteSpace(meta.Path) ? null : BuildSafeFullPath(_opt.BasePath, meta.Path);
            var exists = fullPath is not null && System.IO.File.Exists(fullPath);
            var mime = fullPath is null ? null : ResolveMime(meta.MimeType, meta.NomeFile, fullPath);

            return Ok(new
            {
                meta.Id,
                meta.NomeFile,
                meta.MimeType,
                meta.Path,
                FullPath = fullPath,
                Exists = exists,
                ResolvedMime = mime
            });
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
                return NotFound($"Allegato {id} non disponibile: {ex.Message}");
            }
        }

        private async Task<IActionResult> ServeFromDisk(int id, string? src, bool inline, bool previewOnly, CancellationToken ct)
        {
            try
            {
                var meta = await _attRepo.GetMetaAsync(id, src, ct);
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
                    meta = await _attRepo.GetMetaAsync(id, null, ct);
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

                    meta = await _attRepo.GetMetaAsync(id, null, ct);
                    if (meta == null || string.IsNullOrWhiteSpace(meta.FilePath))
                        return NotFound($"Allegato {id} non recuperato.");

                    fullPath = BuildSafeFullPath(_opt.BasePath, meta.FilePath);
                    if (fullPath is null || !System.IO.File.Exists(fullPath))
                        return NotFound($"Allegato {id} non trovato su disco dopo ensure.");
                }

                // MIME: preferisci DB, altrimenti da estensione
                var mime = ResolveMime(meta.MimeType, meta.NomeFile, fullPath);

                if (previewOnly && !IsPreviewable(mime))
                    return StatusCode(StatusCodes.Status415UnsupportedMediaType,
                        "Anteprima disponibile solo per PDF e immagini.");

                Response.Headers["X-Content-Type-Options"] = "nosniff";

                var safeName = string.IsNullOrWhiteSpace(meta.NomeFile) ? "allegato" : meta.NomeFile;
                var dispo = inline ? "inline" : "attachment";

                Response.Headers["Content-Disposition"] =
                    $"{dispo}; filename=\"{safeName}\"; filename*=UTF-8''{Uri.EscapeDataString(safeName)}";

                _logger.LogInformation("ServeFromDisk Id={Id} Path={Path} Mime={Mime} Inline={Inline} PreviewOnly={PreviewOnly}",
                    id, fullPath, mime, inline, previewOnly);

                return PhysicalFile(fullPath, mime, enableRangeProcessing: true);
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

        private async Task<IActionResult> ServeSentFromDisk(int id, bool inline, bool previewOnly, CancellationToken ct)
        {
            try
            {
                var meta = await _attRepo.GetSentAttachmentMetaAsync(id, ct);
                if (meta is null)
                    return NotFound($"Allegato inviato {id} non presente a DB.");

                if (string.IsNullOrWhiteSpace(_opt.BasePath))
                    return Problem("Attachments:BasePath non configurato.");

                if (string.IsNullOrWhiteSpace(meta.Path))
                    return NotFound("Allegato non disponibile");

                var fullPath = BuildSafeFullPath(_opt.BasePath, meta.Path);
                if (fullPath is null)
                    return BadRequest("PATH non valido (path traversal).");

                if (!System.IO.File.Exists(fullPath))
                    return NotFound("Allegato non disponibile");

                var mime = ResolveMime(meta.MimeType, meta.NomeFile, fullPath);

                if (previewOnly && !IsPreviewable(mime))
                    return StatusCode(StatusCodes.Status415UnsupportedMediaType,
                        "Anteprima disponibile solo per PDF e immagini.");

                Response.Headers["X-Content-Type-Options"] = "nosniff";

                var safeName = string.IsNullOrWhiteSpace(meta.NomeFile) ? "allegato" : meta.NomeFile;
                var dispo = inline ? "inline" : "attachment";

                Response.Headers["Content-Disposition"] =
                    $"{dispo}; filename=\"{safeName}\"; filename*=UTF-8''{Uri.EscapeDataString(safeName)}";

                _logger.LogInformation("ServeSentFromDisk Id={Id} Path={Path} Mime={Mime} Inline={Inline} PreviewOnly={PreviewOnly}",
                    id, fullPath, mime, inline, previewOnly);

                return PhysicalFile(fullPath, mime, enableRangeProcessing: true);
            }
            catch (OperationCanceledException)
            {
                return StatusCode(StatusCodes.Status499ClientClosedRequest);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Errore ServeSentFromDisk allegato inviato Id={Id}", id);
                return NotFound("Allegato non disponibile");
            }
        }

        private string ResolveMime(string? dbMime, string? fileName, string fullPath)
        {
            var mime = (dbMime ?? "").Trim();
            var ext = Path.GetExtension(fileName ?? fullPath)?.ToLowerInvariant();

            var byExt = ext switch
            {
                ".pdf" => "application/pdf",
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".webp" => "image/webp",
                ".svg" => "image/svg+xml",
                ".txt" => "text/plain",
                ".html" or ".htm" => "text/html",
                ".csv" => "text/csv",
                ".eml" => "message/rfc822",
                ".doc" => "application/msword",
                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ".xls" => "application/vnd.ms-excel",
                ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(byExt))
                return byExt;

            // Se DB dice octet-stream o è vuoto, provo a dedurre
            if (string.IsNullOrWhiteSpace(mime) ||
                mime.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(fileName) && _ct.TryGetContentType(fileName, out var byName))
                    return byName;

                if (_ct.TryGetContentType(fullPath, out var byPath))
                    return byPath;

                return ext switch
                {
                    ".pdf" => "application/pdf",
                    ".png" => "image/png",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".gif" => "image/gif",
                    ".txt" => "text/plain",
                    ".html" or ".htm" => "text/html",
                    _ => "application/octet-stream"
                };
            }

            return mime;
        }

        private static string BuildEmlHtml(MimeKit.MimeMessage msg, int emlAttachmentId)
        {
            static string Enc(string? v) => System.Net.WebUtility.HtmlEncode(v ?? "");

            var subject = Enc(msg.Subject);
            var from = Enc(msg.From?.ToString());
            var to = Enc(msg.To?.ToString());
            var cc = Enc(msg.Cc?.ToString());
            var date = msg.Date.ToLocalTime().ToString("dd/MM/yyyy HH:mm");

            var body = RenderMimeEntity(msg.Body);
            body = StripActiveContent(body);

            var attachmentViews = GetAllAttachments(msg.Body);

            var attachments = attachmentViews.Select(a =>
            {
                var size = a.Size > 0
                    ? $" <span class='att-size'>({a.Size / 1024.0:0.#} KB)</span>"
                    : "";

                var canPreview = IsPreviewableEmlPart(a.Mime, a.FileName);

                var previewBtn = canPreview
    ? $"<button type='button' class='att-btn' onclick=\"openPreview('/api/attachments/{emlAttachmentId}/eml-part/{a.Index}/viewer')\">Anteprima</button>"
    : "";

                var downloadBtn =
                    $"<a class='att-btn' href='/api/attachments/{emlAttachmentId}/eml-part/{a.Index}/download'>Scarica</a>";

                return $@"
<div class='att-chip'>
    <span class='att-icon'>📎</span>
    <span>{Enc(a.FileName)}</span>
    {size}
    {previewBtn}
    {downloadBtn}
</div>";
            }).ToList();

            var attachmentsHtml = attachments.Count > 0
                ? $@"
<div class='attachments'>
    <div class='attachments-title'>{attachments.Count} allegati</div>
    <div class='attachments-grid'>
        {string.Join("", attachments)}
    </div>
</div>"
                : "";

            return $@"
<!doctype html>
<html>
<head>
<meta charset='utf-8'>
<title>{subject}</title>
<style>
body {{
    margin: 0;
    background: #f5f7fb;
    font-family: Arial, sans-serif;
    color: #111827;
}}

.page {{
    padding: 24px;
}}

.card {{
    max-width: 1280px;
    margin: 0 auto;
    background: #fff;
    border: 1px solid #dbe3ef;
    border-radius: 14px;
    padding: 22px;
}}

h1 {{
    font-size: 22px;
    margin: 0 0 16px 0;
}}

.meta {{
    font-size: 13px;
    margin-bottom: 14px;
}}

.meta div {{
    margin: 4px 0;
}}

.attachments {{
    border-top: 1px solid #e5e7eb;
    border-bottom: 1px solid #e5e7eb;
    padding: 10px 0;
    margin: 10px 0 18px 0;
}}

.attachments-title {{
    font-size: 13px;
    color: #6b7280;
    margin-bottom: 8px;
}}

.attachments-grid {{
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
}}

.att-chip {{
    display: inline-flex;
    align-items: center;
    gap: 7px;
    border: 1px solid #dbe3ef;
    border-radius: 8px;
    padding: 7px 10px;
    background: #f8fafc;
    font-size: 13px;
}}

.att-size {{
    color: #6b7280;
}}

.att-btn {{
    margin-left: 8px;
    padding: 3px 8px;
    border-radius: 999px;
    background: #dbeafe;
    color: #1d4ed8;
    font-size: 12px;
    font-weight: 700;
    text-decoration: none;
}}

.att-btn:hover {{
    background: #bfdbfe;
}}

.body {{
    font-size: 14px;
    line-height: 1.45;
}}

pre {{
    white-space: pre-wrap;
    font-family: Arial, sans-serif;
}}
.preview-overlay {{position: fixed;
    inset: 0;
    background: rgba(15, 23, 42, .65);
    z-index: 9999;
    display: none;
    align-items: center;
    justify-content: center;
    padding: 24px;
}}

.preview-box {{width: 92vw;
    height: 90vh;
    background: white;
    border-radius: 14px;
    overflow: hidden;
    position: relative;
}}

.preview-box iframe {{width: 100%;
    height: 100%;
    border: 0;
}}

.preview-close {{position: absolute;
    top: 10px;
    right: 14px;
    z-index: 2;
    border: 0;
    background: #ef4444;
    color: white;
    padding: 7px 12px;
    border-radius: 999px;
    cursor: pointer;
    font-weight: 700;
}}
</style>
</head>
<body>
<div class='page'>
<div class='card'>
    <h1>{subject}</h1>

    <div class='meta'>
        <div><b>From:</b> {from}</div>
        <div><b>To:</b> {to}</div>
        {(string.IsNullOrWhiteSpace(cc) ? "" : $"<div><b>Cc:</b> {cc}</div>")}
        <div><b>Date:</b> {date}</div>
    </div>

    {attachmentsHtml}

    <div class='body'>
        {body}
    </div>
</div>
</div>
<div id='previewOverlay' class='preview-overlay'>
    <div class='preview-box'>
        <button class='preview-close' onclick='closePreview()'>Chiudi</button>
        <iframe id='previewFrame'></iframe>
    </div>
</div>

<script>
function openPreview(src) {{
    document.getElementById('previewFrame').src = src;
    document.getElementById('previewOverlay').style.display = 'flex';
}}

function closePreview() {{
    document.getElementById('previewFrame').src = '';
    document.getElementById('previewOverlay').style.display = 'none';
}}
</script>
</body>
</html>";
        }

        private static string RenderMimeEntity(MimeKit.MimeEntity entity)
        {
            static string Enc(string? v) => System.Net.WebUtility.HtmlEncode(v ?? "");

            if (entity is MimeKit.TextPart textPart)
            {
                var text = textPart.Text ?? "";

                if (textPart.IsHtml)
                    return text;

                return "<pre>" + Enc(text) + "</pre>";
            }

            if (entity is MimeKit.MessagePart messagePart && messagePart.Message != null)
            {
                return BuildNestedMessageHtml(messagePart.Message);
            }

            if (entity is MimeKit.Multipart multipart)
            {
                var parts = new List<string>();

                foreach (var part in multipart)
                {
                    if (part is MimeKit.MimePart mp)
                    {
                        var mime = mp.ContentType?.MimeType ?? "";

                        // Skip solo allegati veri
                        var disposition = mp.ContentDisposition?.Disposition;

                        var isRealAttachment =
                            string.Equals(disposition, "attachment", StringComparison.OrdinalIgnoreCase);

                        if (isRealAttachment)
                            continue;

                        // Renderizza html/text anche se hanno filename
                        if (!mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
                            continue;
                    }

                    var rendered = RenderMimeEntity(part);

                    if (!string.IsNullOrWhiteSpace(rendered))
                        parts.Add(rendered);
                }

                return string.Join("<hr>", parts);
            }

            return "";
        }

        private static string BuildNestedMessageHtml(MimeKit.MimeMessage msg)
        {
            static string Enc(string? v) => System.Net.WebUtility.HtmlEncode(v ?? "");

            var subject = Enc(msg.Subject);
            var from = Enc(msg.From?.ToString());
            var to = Enc(msg.To?.ToString());
            var date = msg.Date.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
            var body = RenderMimeEntity(msg.Body);

            return $@"
<div class='nested-message'>
    <div style='margin:12px 0;font-size:13px;'>
        <div><b>From:</b> {from}</div>
        <div><b>Subject:</b> {subject}</div>
        <div><b>Date:</b> {date}</div>
        <div><b>To:</b> {to}</div>
    </div>
    <div>{body}</div>
</div>";
        }

        private static bool IsPreviewableEmlPart(string? mime, string? fileName)
        {
            mime ??= "";

            if (mime.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
                return true;

            if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return true;

            var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();

            return ext is ".pdf" or ".png" or ".jpg" or ".jpeg" or ".gif";
        }

        private static List<EmlAttachmentView> GetAllAttachments(MimeKit.MimeEntity entity)
        {
            var list = new List<EmlAttachmentView>();
            CollectAttachments(entity, list);
            return list;
        }

        private static void CollectAttachments(MimeKit.MimeEntity entity, List<EmlAttachmentView> list)
        {
            if (entity is MimeKit.Multipart multipart)
            {
                foreach (var part in multipart)
                    CollectAttachments(part, list);

                return;
            }

            if (entity is MimeKit.MessagePart messagePart)
            {
                var name =
                    messagePart.ContentDisposition?.FileName ??
                    messagePart.ContentType?.Name ??
                    "email_allegata.eml";

                list.Add(new EmlAttachmentView(
                    list.Count,
                    name,
                    "message/rfc822",
                    0
                ));

                if (messagePart.Message?.Body != null)
                    CollectAttachments(messagePart.Message.Body, list);

                return;
            }

            if (entity is MimeKit.MimePart mimePart)
            {
                var name =
                    mimePart.FileName ??
                    mimePart.ContentDisposition?.FileName ??
                    mimePart.ContentType?.Name;

                var disposition = mimePart.ContentDisposition?.Disposition;

                var isAttachment =
                    !string.IsNullOrWhiteSpace(name) ||
                    string.Equals(disposition, "attachment", StringComparison.OrdinalIgnoreCase);

                if (!isAttachment)
                    return;

                var mime = mimePart.ContentType?.MimeType ?? "application/octet-stream";

                long size = 0;

                try
                {
                    using var ms = new MemoryStream();
                    mimePart.Content.DecodeTo(ms);
                    size = ms.Length;
                }
                catch { }

                list.Add(new EmlAttachmentView(
                    list.Count,
                    name ?? "allegato",
                    mime,
                    size
                ));
            }
        }

        private static List<MimeKit.MimeEntity> GetAllAttachmentEntities(MimeKit.MimeEntity entity)
        {
            var list = new List<MimeKit.MimeEntity>();
            CollectAttachmentEntities(entity, list);
            return list;
        }

        private static void CollectAttachmentEntities(
            MimeKit.MimeEntity entity,
            List<MimeKit.MimeEntity> list)
        {
            if (entity is MimeKit.Multipart multipart)
            {
                foreach (var part in multipart)
                    CollectAttachmentEntities(part, list);

                return;
            }

            if (entity is MimeKit.MessagePart messagePart)
            {
                list.Add(messagePart);

                if (messagePart.Message?.Body != null)
                    CollectAttachmentEntities(messagePart.Message.Body, list);

                return;
            }

            if (entity is MimeKit.MimePart mimePart)
            {
                var name =
                    mimePart.FileName ??
                    mimePart.ContentDisposition?.FileName ??
                    mimePart.ContentType?.Name;

                var disposition = mimePart.ContentDisposition?.Disposition;

                var isAttachment =
                    !string.IsNullOrWhiteSpace(name) ||
                    string.Equals(disposition, "attachment", StringComparison.OrdinalIgnoreCase);

                if (isAttachment)
                    list.Add(mimePart);
            }
        }

        private async Task<IActionResult> ServeEmlPart(
     int emlId,
     int index,
     bool inline,
     CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(_opt.BasePath))
                return Problem("Attachments:BasePath non configurato.");

            var saved = await EnsureEmlPartSavedAsync(emlId, index, ct);

            if (saved == null)
                return NotFound();

            var disposition = inline ? "inline" : "attachment";

            Response.Headers.Remove("Content-Disposition");
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            Response.Headers["Content-Disposition"] =
                $"{disposition}; filename=\"{saved.FileName}\"; filename*=UTF-8''{Uri.EscapeDataString(saved.FileName)}";

            _logger.LogInformation(
                "ServeEmlPart saved emlId={EmlId}, index={Index}, inline={Inline}, path={Path}, mime={Mime}",
                emlId,
                index,
                inline,
                saved.FullPath,
                saved.Mime);

            return PhysicalFile(
                saved.FullPath,
                saved.Mime,
                enableRangeProcessing: true
            );
        }

        private static string StripActiveContent(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
                return html;

            html = System.Text.RegularExpressions.Regex.Replace(
                html,
                @"<\s*script\b[^>]*>.*?<\s*/\s*script\s*>",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.Singleline);

            html = System.Text.RegularExpressions.Regex.Replace(
                html,
                @"\son\w+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            return html;
        }

        private async Task<byte[]?> GetAttachmentBytes(int id, CancellationToken ct = default)
        {
            var meta = await _attRepo.GetMetaAsync(id, null, ct);

            if (meta == null)
                return null;

            // Se FILE_PATH manca provo a recuperare l'allegato
            if (string.IsNullOrWhiteSpace(meta.FilePath))
            {
                var ok = await _fetch.EnsureSingleAttachmentAsync(id, ct);

                if (!ok)
                    return null;

                meta = await _attRepo.GetMetaAsync(id, null, ct);

                if (meta == null || string.IsNullOrWhiteSpace(meta.FilePath))
                    return null;
            }

            if (string.IsNullOrWhiteSpace(_opt.BasePath))
                return null;

            var fullPath = BuildSafeFullPath(_opt.BasePath, meta.FilePath);

            // Se il file non esiste provo una seconda volta a recuperarlo
            if (fullPath == null || !System.IO.File.Exists(fullPath))
            {
                var ok = await _fetch.EnsureSingleAttachmentAsync(id, ct);

                if (!ok)
                    return null;

                meta = await _attRepo.GetMetaAsync(id, null, ct);

                if (meta == null || string.IsNullOrWhiteSpace(meta.FilePath))
                    return null;

                fullPath = BuildSafeFullPath(_opt.BasePath, meta.FilePath);

                if (fullPath == null || !System.IO.File.Exists(fullPath))
                    return null;
            }

            return await System.IO.File.ReadAllBytesAsync(fullPath, ct);
        }

        private static string ResolveMimeForEmlPart(string? fileName, string? currentMime)
        {
            var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();

            return ext switch
            {
                ".pdf" => "application/pdf",
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".txt" => "text/plain; charset=utf-8",
                ".html" or ".htm" => "text/html; charset=utf-8",
                ".csv" => "text/csv; charset=utf-8",
                _ => string.IsNullOrWhiteSpace(currentMime)
                    ? "application/octet-stream"
                    : currentMime
            };
        }

        private async Task<EmlSavedPart?> EnsureEmlPartSavedAsync(
    int emlId,
    int index,
    CancellationToken ct)
        {
            var fileBytes = await GetAttachmentBytes(emlId, ct);

            if (fileBytes == null || fileBytes.Length == 0)
                return null;

            using var ms = new MemoryStream(fileBytes);
            var msg = await MimeKit.MimeMessage.LoadAsync(ms, ct);

            var parts = GetAllAttachmentEntities(msg.Body);

            if (index < 0 || index >= parts.Count)
                return null;

            var part = parts[index];

            var fileName =
                (part is MimeKit.MimePart mpName ? mpName.FileName : null) ??
                part.ContentDisposition?.FileName ??
                part.ContentType?.Name ??
                $"allegato_{index}";

            fileName = Path.GetFileName(fileName);

            var mime = part.ContentType?.MimeType ?? "application/octet-stream";

            if (part is MimeKit.MessagePart && !fileName.EndsWith(".eml", StringComparison.OrdinalIgnoreCase))
                fileName += ".eml";

            mime = ResolveMimeForEmlPart(fileName, mime);

            var relativeDir = Path.Combine("eml-preview", emlId.ToString(), index.ToString());
            var fullDir = Path.Combine(_opt.BasePath, relativeDir);

            Directory.CreateDirectory(fullDir);

            var fullPath = Path.Combine(fullDir, fileName);
            var relativePath = Path.Combine(relativeDir, fileName);

            if (!System.IO.File.Exists(fullPath))
            {
                await using var outMs = new MemoryStream();

                if (part is MimeKit.MimePart mp)
                {
                    await mp.Content.DecodeToAsync(outMs, ct);
                }
                else if (part is MimeKit.MessagePart messagePart && messagePart.Message != null)
                {
                    await messagePart.Message.WriteToAsync(outMs, ct);
                    mime = "message/rfc822";
                }
                else
                {
                    await part.WriteToAsync(outMs, ct);
                }

                await System.IO.File.WriteAllBytesAsync(fullPath, outMs.ToArray(), ct);
            }

            return new EmlSavedPart(fullPath, relativePath, fileName, mime);
        }
    }

}
