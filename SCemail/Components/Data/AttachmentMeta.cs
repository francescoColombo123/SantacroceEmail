namespace SCemail.Components.Data;

public sealed record AttachmentMeta(
    int Id,
    int EmailId,
    string NomeFile,
    string? MimeType,
    string? FilePath,
    long? FileSize
);
