using Dapper;
using Oracle.ManagedDataAccess.Client;

namespace SCemail.Components.Data;

public sealed class EmailAttachmentsRepository : IEmailAttachmentsRepository
{
    private readonly string _connStr;
    public EmailAttachmentsRepository(IConfiguration cfg) => _connStr = cfg.GetConnectionString("OracleDb")!;

    private OracleConnection Open() => new(_connStr);

    private sealed class AttachmentRow
    {
        public decimal Id { get; set; }
        public decimal EmailId { get; set; }
        public string NomeFile { get; set; } = "";
        public string? MimeType { get; set; }
        public string? FilePath { get; set; }
        public decimal? FileSize { get; set; }
    }

    public async Task<AttachmentMeta?> GetMetaAsync(int allegatoId, CancellationToken ct)
    {
        const string sql = @"
SELECT
    a.ID        AS Id,
    a.EMAIL_ID  AS EmailId,
    a.NOME_FILE AS NomeFile,
    a.MIME_TYPE AS MimeType,
    a.FILE_PATH AS FilePath,
    a.FILE_SIZE AS FileSize
FROM SGAPP.EMAIL_ALLEGATI a
WHERE a.ID = :id";

        await using var con = Open();

        // Dapper supporta CancellationToken via CommandDefinition
        var cmd = new CommandDefinition(sql, new { id = allegatoId }, cancellationToken: ct);

        var r = await con.QueryFirstOrDefaultAsync<AttachmentRow>(cmd);
        if (r is null) return null;

        return new AttachmentMeta(
            Id: (int)r.Id,
            EmailId: (int)r.EmailId,
            NomeFile: r.NomeFile,
            MimeType: r.MimeType,
            FilePath: r.FilePath,
            FileSize: r.FileSize is null ? null : (long?)r.FileSize.Value
        );
    }
}
