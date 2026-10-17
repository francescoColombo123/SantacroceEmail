using Oracle.ManagedDataAccess.Client;
using System.Data;

// Ricalcola THREAD_KEY delle email già salvate con la STESSA regola usata in ingestione
// (EmailFetchService.ResolveThreadKeyAsync), così lista, conteggi e dettaglio coincidono:
//
//   1. GMAIL_THREAD_ID presente  -> THREAD_KEY = GMAIL_THREAD_ID (Gmail è autoritativo)
//   2. altrimenti                -> THREAD_KEY del primo padre presente nella casella
//                                   (In-Reply-To, poi References dal più recente)
//   3. nessun padre trovato      -> THREAD_KEY = MESSAGE_ID
//   4. nessun header salvato     -> THREAD_KEY invariato (vedi BackfillThreadHeadersAsync,
//                                   che prima li recupera da IMAP)
//
// A differenza del vecchio BackfillHistoricalThreadsAsync non modifica mai GMAIL_THREAD_ID
// (quindi non fonde thread Gmail diversi) e lavora in memoria per casella:
// una sola lettura e UPDATE solo delle righe che cambiano.
public sealed class ThreadKeyRealigner
{
    public sealed record Change(
        string Table,
        int Id,
        int CasellaId,
        string? OldKey,
        string NewKey,
        string? Subject);

    public sealed class Report
    {
        public int Rows { get; set; }
        public List<Change> Changes { get; } = new();
        public Dictionary<int, int> ChangesByCasella { get; } = new();
    }

    private sealed class Row
    {
        public string Table { get; init; } = "";
        public int Id { get; init; }
        public string MessageId { get; init; } = "";
        public string? InReplyTo { get; init; }
        public string? References { get; init; }
        public string? GmailThreadId { get; init; }
        public string? ThreadKey { get; init; }
        public string? Subject { get; init; }

        // THREAD_ALIGNED = 'N' con UID: BackfillThreadHeadersAsync non l'ha ancora verificata su IMAP.
        public bool PendingImapCheck { get; init; }

        public string? Resolved;
        public bool Visiting;
    }

    private const int UpdateBatchSize = 500;

    private readonly ILogger? _logger;

    public ThreadKeyRealigner(ILogger? logger = null) => _logger = logger;

    // apply = false: solo simulazione, nessuna scrittura.
    public async Task<Report> RunAsync(
        OracleConnection conn,
        bool apply,
        CancellationToken ct = default,
        int? onlyCasellaId = null)
    {
        var report = new Report();

        foreach (var casellaId in (await LoadCaselleAsync(conn, ct)).Where(x => onlyCasellaId == null || x == onlyCasellaId))
        {
            ct.ThrowIfCancellationRequested();

            var rows = await LoadRowsAsync(conn, casellaId, ct);
            report.Rows += rows.Count;

            var changes = Compute(casellaId, rows);

            if (changes.Count == 0)
                continue;

            report.Changes.AddRange(changes);
            report.ChangesByCasella[casellaId] = changes.Count;

            if (apply)
                await ApplyAsync(conn, changes, ct);

            _logger?.LogInformation(
                "Riallineamento THREAD_KEY casella={Cid}: righe={Rows} modifiche={Changes} apply={Apply}",
                casellaId, rows.Count, changes.Count, apply);
        }

        return report;
    }

    private static List<Change> Compute(int casellaId, List<Row> rows)
    {
        // Message-ID -> riga. Con doppioni (es. copia in ricevute e inviate)
        // preferisco quella con thread Gmail, poi la ricevuta.
        var byMid = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows
                     .OrderByDescending(r => !string.IsNullOrWhiteSpace(r.GmailThreadId))
                     .ThenBy(r => r.Table == "R" ? 0 : 1))
        {
            byMid.TryAdd(row.MessageId, row);
        }

        string Resolve(Row row)
        {
            if (row.Resolved != null)
                return row.Resolved;

            if (!string.IsNullOrWhiteSpace(row.GmailThreadId))
                return row.Resolved = row.GmailThreadId.Trim();

            // Header non ancora riletti da IMAP (per Gmail arriverà il thread id):
            // la riga resta com'è e verrà ricalcolata nelle notti successive.
            if (row.PendingImapCheck && !string.IsNullOrWhiteSpace(row.ThreadKey))
                return row.Resolved = row.ThreadKey.Trim();

            if (row.Visiting)
                return row.MessageId; // ciclo di riferimenti: mi fermo

            // Stesso messaggio salvato due volte (ricevute + inviate):
            // segue la copia "principale", così finiscono nello stesso thread.
            if (byMid.TryGetValue(row.MessageId, out var canonical) && !ReferenceEquals(canonical, row))
                return row.Resolved = Resolve(canonical);

            // Nessun dato di thread (mail storiche salvate senza header, non recuperabili
            // da IMAP): non c'è nulla da ricalcolare, si tiene la chiave attuale.
            if (string.IsNullOrWhiteSpace(row.InReplyTo)
                && string.IsNullOrWhiteSpace(row.References)
                && !string.IsNullOrWhiteSpace(row.ThreadKey))
            {
                return row.Resolved = row.ThreadKey.Trim();
            }

            row.Visiting = true;

            string? key = null;

            foreach (var candidate in CandidateParents(row))
            {
                if (byMid.TryGetValue(candidate, out var parent) && !ReferenceEquals(parent, row))
                {
                    key = Resolve(parent);
                    break;
                }
            }

            row.Visiting = false;

            return row.Resolved = key ?? row.MessageId;
        }

        var changes = new List<Change>();

        foreach (var row in rows)
        {
            var newKey = Resolve(row);

            if (!string.Equals(
                    (row.ThreadKey ?? "").Trim(),
                    newKey,
                    StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(new Change(row.Table, row.Id, casellaId, row.ThreadKey, newKey, row.Subject));
            }
        }

        return changes;
    }

    // Stesso ordine di EmailFetchService.GetThreadCandidateMessageIds.
    private static IEnumerable<string> CandidateParents(Row row)
    {
        var inReplyTo = EmailFetchService.NormalizeMessageId(row.InReplyTo);

        if (!string.IsNullOrWhiteSpace(inReplyTo))
            yield return inReplyTo;

        var refs = EmailFetchService.ExtractMessageIdsFromReferences(row.References);

        for (var i = refs.Count - 1; i >= 0; i--)
            yield return refs[i];
    }

    private static async Task<List<int>> LoadCaselleAsync(OracleConnection conn, CancellationToken ct)
    {
        const string sql = @"
SELECT DISTINCT CASELLA_ID FROM SGAPP.EMAIL_RICEVUTE WHERE CASELLA_ID IS NOT NULL
UNION
SELECT DISTINCT CASELLA_ID FROM SGAPP.EMAIL_INVIATE WHERE CASELLA_ID IS NOT NULL";

        var result = new List<int>();

        await using var cmd = new OracleCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
            result.Add(Convert.ToInt32(reader.GetValue(0)));

        return result;
    }

    private static async Task<List<Row>> LoadRowsAsync(OracleConnection conn, int casellaId, CancellationToken ct)
    {
        // References: bastano gli ultimi 4000 caratteri (i padri più vicini sono in fondo).
        // Le EML importate come allegato hanno un thread proprio e restano escluse.
        // Le colonne non hanno lo stesso tipo nelle due tabelle (CLOB / VARCHAR2):
        // tutto passa da TO_CLOB + DBMS_LOB.SUBSTR per avere VARCHAR2 in entrambe.
        const string cols = @"
       DBMS_LOB.SUBSTR(TO_CLOB(MESSAGE_ID), 1000, 1) AS MESSAGE_ID,
       DBMS_LOB.SUBSTR(TO_CLOB(IN_REPLY_TO), 1000, 1) AS IN_REPLY_TO,
       DBMS_LOB.SUBSTR(TO_CLOB(REFERENCES_HDR), 4000,
           GREATEST(1, NVL(DBMS_LOB.GETLENGTH(TO_CLOB(REFERENCES_HDR)), 0) - 3999)) AS REFS,
       DBMS_LOB.SUBSTR(TO_CLOB(GMAIL_THREAD_ID), 500, 1) AS GMAIL_THREAD_ID,
       DBMS_LOB.SUBSTR(TO_CLOB(THREAD_KEY), 1000, 1) AS THREAD_KEY,
       DBMS_LOB.SUBSTR(TO_CLOB(OGGETTO), 200, 1) AS OGGETTO,
       CASE WHEN THREAD_ALIGNED = 'N' AND MESSAGE_UID IS NOT NULL AND FOLDER_PATH IS NOT NULL
                 AND GMAIL_THREAD_ID IS NULL
            THEN 'Y' ELSE 'N' END AS PENDING_IMAP";

        const string sql = $@"
SELECT 'R' AS TBL, ID, {cols}
  FROM SGAPP.EMAIL_RICEVUTE
 WHERE CASELLA_ID = :p_cid
   AND NVL(IS_EML_IMPORTATA, 'N') = 'N'
UNION ALL
SELECT 'I' AS TBL, ID, {cols}
  FROM SGAPP.EMAIL_INVIATE
 WHERE CASELLA_ID = :p_cid";

        var rows = new List<Row>();

        await using var cmd = new OracleCommand(sql, conn) { BindByName = true, FetchSize = 4 * 1024 * 1024 };
        cmd.Parameters.Add("p_cid", OracleDbType.Int32).Value = casellaId;

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        static string? Str(IDataRecord r, int i) => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i));

        while (await reader.ReadAsync(ct))
        {
            var table = Str(reader, 0)!;
            var id = Convert.ToInt32(reader.GetValue(1));

            var mid = EmailFetchService.NormalizeMessageId(Str(reader, 2))
                      ?? $"SINGLE_{table}_{id}";

            rows.Add(new Row
            {
                Table = table,
                Id = id,
                MessageId = mid,
                InReplyTo = Str(reader, 3),
                References = Str(reader, 4),
                GmailThreadId = Str(reader, 5),
                ThreadKey = Str(reader, 6),
                Subject = Str(reader, 7),
                PendingImapCheck = Str(reader, 8) == "Y"
            });
        }

        return rows;
    }

    private static async Task ApplyAsync(OracleConnection conn, List<Change> changes, CancellationToken ct)
    {
        foreach (var group in changes.GroupBy(c => c.Table))
        {
            var table = group.Key == "R" ? "SGAPP.EMAIL_RICEVUTE" : "SGAPP.EMAIL_INVIATE";

            foreach (var batch in group.Chunk(UpdateBatchSize))
            {
                await using var tx = (OracleTransaction)await conn.BeginTransactionAsync(ct);

                await using var cmd = new OracleCommand(
                    $"UPDATE {table} SET THREAD_KEY = :p_key WHERE ID = :p_id", conn)
                {
                    BindByName = true,
                    ArrayBindCount = batch.Length,
                    Transaction = tx
                };

                cmd.Parameters.Add("p_key", OracleDbType.Varchar2, 500).Value =
                    batch.Select(c => c.NewKey).ToArray();

                cmd.Parameters.Add("p_id", OracleDbType.Int32).Value =
                    batch.Select(c => c.Id).ToArray();

                await cmd.ExecuteNonQueryAsync(ct);
                await tx.CommitAsync(ct);
            }
        }
    }
}
