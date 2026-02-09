using System.Threading;

namespace SCemail.Components.Data;

public interface IEmailAttachmentsRepository
{
    Task<AttachmentMeta?> GetMetaAsync(int allegatoId, CancellationToken ct);
}
