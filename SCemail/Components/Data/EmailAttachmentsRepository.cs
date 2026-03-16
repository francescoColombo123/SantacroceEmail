using Dapper;
using Oracle.ManagedDataAccess.Client;
using static Org.BouncyCastle.Math.EC.ECCurve;

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

    public sealed class SentAttachmentMetaDto
    {
        public int Id { get; set; }
        public string NomeFile { get; set; } = "";
        public string MimeType { get; set; } = "";
        public string? Path { get; set; }
    }
    public sealed class AttachmentContextItemDto
    {
        public decimal Id { get; set; }
        public decimal EmailId { get; set; }
        public string? NomeFile { get; set; }
        public string? MimeType { get; set; }
    }

    public async Task<AttachmentMeta?> GetMetaAsync(int allegatoId, string? src, CancellationToken ct)
    {
        bool isSent = string.Equals(src, "sent", StringComparison.OrdinalIgnoreCase);

        const string sqlRicevuti = @"
SELECT a.ID AS Id, a.EMAIL_ID AS EmailId, a.NOME_FILE AS NomeFile,
       a.MIME_TYPE AS MimeType, a.FILE_PATH AS FilePath, a.FILE_SIZE AS FileSize
FROM SGAPP.EMAIL_ALLEGATI a
WHERE a.ID = :id";

        const string sqlInviati = @"
SELECT a.ID AS Id, a.EMAIL_ID AS EmailId, a.NOME_FILE AS NomeFile,
       a.MIME_TYPE AS MimeType, a.FILE_PATH AS FilePath, a.FILE_SIZE AS FileSize
FROM SGAPP.INVIATA_ALLEGATI a
WHERE a.ID = :id";

        var sql = isSent ? sqlInviati : sqlRicevuti;

        await using var con = Open();
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

    public async Task<SentAttachmentMetaDto?> GetSentAttachmentMetaAsync(int id, CancellationToken ct)
    {

        await using var con = Open();
        const string sql = """
        SELECT
            ID        AS Id,
            NOME_FILE AS NomeFile,
            MIME_TYPE AS MimeType,
            PATH      AS Path
        FROM INVIATA_ALLEGATI
        WHERE ID = :id
        """;

        var cmd = new Dapper.CommandDefinition(sql, new { id }, cancellationToken: ct);
        return await con.QueryFirstOrDefaultAsync<SentAttachmentMetaDto>(cmd);
    }

    public async Task<List<AttachmentContextItemDto>> GetAttachmentsOfSameEmailAsync(int attachmentId, CancellationToken ct)
    {
        await using var con = Open();

        const string sql = """
    SELECT
        a.ID        AS Id,
        a.EMAIL_ID  AS EmailId,
        a.NOME_FILE AS NomeFile,
        a.MIME_TYPE AS MimeType
    FROM SGAPP.EMAIL_ALLEGATI a
    WHERE a.EMAIL_ID = (
        SELECT x.EMAIL_ID
        FROM SGAPP.EMAIL_ALLEGATI x
        WHERE x.ID = :attachmentId
    )
    ORDER BY a.ID
    """;

        var cmd = new CommandDefinition(sql, new { attachmentId }, cancellationToken: ct);
        var rows = await con.QueryAsync<AttachmentContextItemDto>(cmd);

        return rows.ToList();
    }

    public async Task<List<SentAttachmentMetaDto>> GetSentAttachmentsOfSameEmailAsync(
    int attachmentId,
    CancellationToken ct)
    {
        const string sql = @"
        SELECT a.ID,
               a.NOME_FILE   AS NomeFile,
               a.MIME_TYPE   AS MimeType,
               a.PATH        AS Path
        FROM INVIATA_ALLEGATI a
        WHERE a.EMAIL_ID = (
            SELECT EMAIL_ID
            FROM INVIATA_ALLEGATI
            WHERE ID = :attachmentId
        )
        ORDER BY a.ID
    ";

        using var conn = Open();
        var rows = await conn.QueryAsync<SentAttachmentMetaDto>(
            new CommandDefinition(
                sql,
                new { attachmentId },
                cancellationToken: ct));

        return rows.ToList();
    }
}
