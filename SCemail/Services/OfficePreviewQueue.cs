using System.Collections.Concurrent;
using System.Threading.Channels;

namespace SCemail.Services;

public sealed record OfficePreviewJob(
    int AttachmentId,
    string FullPath,
    string SourceType)
{
    public string Key =>
        $"{SourceType.Trim().ToLowerInvariant()}:{AttachmentId}";
}

public interface IOfficePreviewQueue
{
    bool TryEnqueue(
        int attachmentId,
        string fullPath,
        string sourceType);

    ValueTask<OfficePreviewJob> DequeueAsync(
        CancellationToken cancellationToken);

    void Complete(OfficePreviewJob job);
}

public sealed class OfficePreviewQueue
    : IOfficePreviewQueue
{
    private static readonly HashSet<string>
        SupportedExtensions =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ".doc",
                ".docx",
                ".xls",
                ".xlsx"
            };

    private readonly Channel<OfficePreviewJob> _channel =
        Channel.CreateUnbounded<OfficePreviewJob>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

    /*
     * Contiene sia i job in attesa
     * sia quello eventualmente in elaborazione.
     */
    private readonly ConcurrentDictionary<string, byte>
        _pending = new(
            StringComparer.OrdinalIgnoreCase);

    public bool TryEnqueue(
        int attachmentId,
        string fullPath,
        string sourceType)
    {
        if (attachmentId <= 0 ||
            string.IsNullOrWhiteSpace(fullPath))
        {
            return false;
        }

        var extension =
            Path.GetExtension(fullPath);

        if (!SupportedExtensions.Contains(extension))
            return false;

        var normalizedSource =
            string.Equals(
                sourceType,
                "sent",
                StringComparison.OrdinalIgnoreCase)
                ? "sent"
                : "received";

        var job =
            new OfficePreviewJob(
                attachmentId,
                fullPath,
                normalizedSource);

        /*
         * Già accodato/in conversione:
         * non inseriamo un duplicato.
         */
        if (!_pending.TryAdd(job.Key, 0))
            return false;

        if (_channel.Writer.TryWrite(job))
            return true;

        /*
         * Scrittura fallita:
         * liberiamo la chiave.
         */
        _pending.TryRemove(
            job.Key,
            out _);

        return false;
    }

    public ValueTask<OfficePreviewJob> DequeueAsync(
        CancellationToken cancellationToken)
    {
        return _channel.Reader.ReadAsync(
            cancellationToken);
    }

    public void Complete(
        OfficePreviewJob job)
    {
        _pending.TryRemove(
            job.Key,
            out _);
    }
}