using System.Threading;
using static SCemail.Components.Data.EmailAttachmentsRepository;

namespace SCemail.Components.Data;

public interface IEmailAttachmentsRepository
{
    Task<AttachmentMeta?> GetMetaAsync(int allegatoId, string? src, CancellationToken ct);
    Task<SentAttachmentMetaDto?> GetSentAttachmentMetaAsync(int id, CancellationToken ct);

    Task<List<AttachmentContextItemDto>> GetAttachmentsOfSameEmailAsync(int attachmentId, CancellationToken ct);
    Task<List<SentAttachmentMetaDto>> GetSentAttachmentsOfSameEmailAsync(int attachmentId, CancellationToken ct);
}
