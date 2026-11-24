using System.Data;
using Dapper;
using Oracle.ManagedDataAccess.Client;

namespace SCemail.Mobile;

public sealed class MobileDevice
{
    public long Id { get; set; }
    public string DeviceUuid { get; set; } = "";
    public string Nome { get; set; } = "";
    public string Proprietario { get; set; } = "";
    public int Attivo { get; set; }
    public bool IsActive => Attivo == 1;
}

public sealed class MobileDeviceStats
{
    public long Id { get; set; }
    public string DeviceUuid { get; set; } = "";
    public string Nome { get; set; } = "";
    public string Proprietario { get; set; } = "";
    public string? NumeroSim { get; set; }
    public string? Modello { get; set; }
    public string? AppVersion { get; set; }
    public string? AndroidVersion { get; set; }
    public int Attivo { get; set; }
    public DateTime? UltimoContatto { get; set; }
    public DateTime CreatedAt { get; set; }
    public int Risposte { get; set; }
    public int Perse { get; set; }
    public int Rifiutate { get; set; }
    public int Uscita { get; set; }
    public bool IsActive => Attivo == 1;
}

public sealed class MobileCallRow
{
    public long Id { get; set; }
    public DateTime DataInizio { get; set; }
    public long DurataSecondi { get; set; }
    public string? NumeroChiamante { get; set; }
    public string Presentazione { get; set; } = "";
    public string Direzione { get; set; } = "";
    public string Esito { get; set; } = "";
    public long DispositivoId { get; set; }
    public string Nome { get; set; } = "";
    public string Proprietario { get; set; } = "";
}

public sealed record CallSearch(
    DateTime FromUtc,
    DateTime ToUtc,
    long? DeviceId,
    string? Status,
    string? Direction,
    string? NumberContains,
    int Limit = 1000);

public enum RegisterOutcome { Registered, InvalidCode, Conflict }

internal sealed class ActivationRow
{
    public long Id { get; set; }
    public long? DispositivoId { get; set; }
    public DateTime Scadenza { get; set; }
    public DateTime? UsatoAt { get; set; }
    public int Revocato { get; set; }
}

internal sealed class CallVersionRow
{
    public long DispositivoId { get; set; }
    public int Versione { get; set; }
}

/// <summary>
/// Accesso Oracle del modulo chiamate (Dapper + ODP.NET, query parametrizzate).
/// Usa la stessa connection string "OracleDb" dell'applicazione.
/// </summary>
public sealed class MobileRepository
{
    private const int OraUniqueViolation = 1;
    private readonly string _connectionString;

    public MobileRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("OracleDb")
            ?? throw new InvalidOperationException("Connection string 'OracleDb' non trovata");
    }

    private async Task<OracleConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new OracleConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    private static CommandDefinition Cmd(string sql, object? args, CancellationToken ct, IDbTransaction? tx = null) =>
        new(sql, args, tx, commandTimeout: 15, cancellationToken: ct);

    // ------------------------------------------------------------------ health

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = await OpenAsync(ct);
            return await conn.ExecuteScalarAsync<int>(Cmd("SELECT 1 FROM DUAL", null, ct)) == 1;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ dispositivi

    public async Task<MobileDevice?> FindDeviceByTokenHashAsync(string tokenHash, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<MobileDevice>(Cmd(@"
            SELECT ID AS Id, DEVICE_UUID AS DeviceUuid, NOME AS Nome, PROPRIETARIO AS Proprietario, ATTIVO AS Attivo
              FROM SGAPP.MOBILE_DISPOSITIVI
             WHERE TOKEN_HASH = :TokenHash", new { TokenHash = tokenHash }, ct));
    }

    public async Task TouchDeviceAsync(long deviceId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await conn.ExecuteAsync(Cmd(@"
            UPDATE SGAPP.MOBILE_DISPOSITIVI
               SET ULTIMO_CONTATTO = SYS_EXTRACT_UTC(SYSTIMESTAMP)
             WHERE ID = :Id", new { Id = deviceId }, ct));
    }

    /// <summary>
    /// Consuma il codice di attivazione e associa il token al dispositivo, in un'unica transazione.
    /// Se il codice è legato a un dispositivo esistente (riassociazione), aggiorna quella riga
    /// mantenendo lo storico delle chiamate sullo stesso ID.
    /// </summary>
    public async Task<(RegisterOutcome Outcome, long DeviceId)> RegisterAsync(
        RegisterDeviceRequest r, string tokenHash, CancellationToken ct)
    {
        var codeHash = MobileTokens.Sha256Hex(MobileTokens.NormalizeActivationCode(r.ActivationCode!));
        var uuid = r.DeviceUuid!.ToLowerInvariant();

        await using var conn = await OpenAsync(ct);
        await using var tx = (OracleTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var att = await conn.QuerySingleOrDefaultAsync<ActivationRow>(Cmd(@"
                SELECT ID AS Id, DISPOSITIVO_ID AS DispositivoId, SCADENZA AS Scadenza, USATO_AT AS UsatoAt, REVOCATO AS Revocato
                  FROM SGAPP.MOBILE_ATTIVAZIONI
                 WHERE CODICE_HASH = :CodeHash
                   FOR UPDATE", new { CodeHash = codeHash }, ct, tx));

            if (att is null || att.UsatoAt != null || att.Revocato == 1 || att.Scadenza < DateTime.UtcNow)
            {
                await tx.RollbackAsync(ct);
                return (RegisterOutcome.InvalidCode, 0);
            }

            var existingId = await conn.QuerySingleOrDefaultAsync<long?>(Cmd(@"
                SELECT ID FROM SGAPP.MOBILE_DISPOSITIVI WHERE DEVICE_UUID = :Uuid FOR UPDATE",
                new { Uuid = uuid }, ct, tx));

            var details = new
            {
                Uuid = uuid,
                Nome = MobileValidation.Clean(r.DeviceName, 100),
                Proprietario = MobileValidation.Clean(r.OwnerName, 100),
                NumeroSim = MobileValidation.Clean(r.SimNumber, 30),
                TokenHash = tokenHash,
                AppVersion = MobileValidation.Clean(r.AppVersion, 30),
                AndroidVersion = MobileValidation.Clean(r.AndroidVersion, 30),
                Modello = MobileValidation.Clean(r.Model, 100),
            };

            long deviceId;
            var targetId = att.DispositivoId ?? existingId;
            if (att.DispositivoId is { } reassignId && existingId is { } other && other != reassignId)
            {
                // L'UUID appartiene già a un altro dispositivo registrato.
                await tx.RollbackAsync(ct);
                return (RegisterOutcome.Conflict, 0);
            }

            if (targetId is { } id)
            {
                await conn.ExecuteAsync(Cmd(@"
                    UPDATE SGAPP.MOBILE_DISPOSITIVI
                       SET DEVICE_UUID = :Uuid, NOME = :Nome, PROPRIETARIO = :Proprietario, NUMERO_SIM = :NumeroSim,
                           TOKEN_HASH = :TokenHash, APP_VERSION = :AppVersion, ANDROID_VERSION = :AndroidVersion,
                           MODELLO = :Modello, ATTIVO = 1, ULTIMO_CONTATTO = SYS_EXTRACT_UTC(SYSTIMESTAMP),
                           UPDATED_AT = SYS_EXTRACT_UTC(SYSTIMESTAMP)
                     WHERE ID = :Id",
                    new { details.Uuid, details.Nome, details.Proprietario, details.NumeroSim, details.TokenHash,
                          details.AppVersion, details.AndroidVersion, details.Modello, Id = id }, ct, tx));
                deviceId = id;
            }
            else
            {
                await conn.ExecuteAsync(Cmd(@"
                    INSERT INTO SGAPP.MOBILE_DISPOSITIVI
                        (DEVICE_UUID, NOME, PROPRIETARIO, NUMERO_SIM, TOKEN_HASH, APP_VERSION, ANDROID_VERSION, MODELLO, ULTIMO_CONTATTO)
                    VALUES
                        (:Uuid, :Nome, :Proprietario, :NumeroSim, :TokenHash, :AppVersion, :AndroidVersion, :Modello, SYS_EXTRACT_UTC(SYSTIMESTAMP))",
                    details, ct, tx));
                deviceId = await conn.ExecuteScalarAsync<long>(Cmd(
                    "SELECT ID FROM SGAPP.MOBILE_DISPOSITIVI WHERE DEVICE_UUID = :Uuid", new { Uuid = uuid }, ct, tx));
            }

            await conn.ExecuteAsync(Cmd(@"
                UPDATE SGAPP.MOBILE_ATTIVAZIONI
                   SET USATO_AT = SYS_EXTRACT_UTC(SYSTIMESTAMP), USATO_DA_UUID = :Uuid, DISPOSITIVO_ID = :DeviceId
                 WHERE ID = :Id", new { Uuid = uuid, DeviceId = deviceId, att.Id }, ct, tx));

            await tx.CommitAsync(ct);
            return (RegisterOutcome.Registered, deviceId);
        }
        catch (OracleException ex) when (ex.Number == OraUniqueViolation)
        {
            await tx.RollbackAsync(CancellationToken.None);
            return (RegisterOutcome.Conflict, 0);
        }
    }

    // ------------------------------------------------------------------ chiamate

    /// <summary>
    /// Inserimento/aggiornamento idempotente su EVENT_UUID con controllo di versione:
    /// un retry con versione uguale o precedente non sovrascrive i dati più recenti.
    /// </summary>
    public async Task<string> UpsertCallAsync(long deviceId, NormalizedCall c, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        for (var attempt = 0; ; attempt++)
        {
            await using var tx = (OracleTransaction)await conn.BeginTransactionAsync(ct);
            try
            {
                var existing = await conn.QuerySingleOrDefaultAsync<CallVersionRow>(Cmd(@"
                    SELECT DISPOSITIVO_ID AS DispositivoId, VERSIONE AS Versione FROM SGAPP.MOBILE_CHIAMATE
                     WHERE EVENT_UUID = :EventUuid FOR UPDATE", new { c.EventUuid }, ct, tx));

                var args = new
                {
                    c.EventUuid, DeviceId = deviceId, c.PhoneNumber, c.Presentation, c.Direction, c.Status,
                    c.RawType, c.StartedUtc, c.EndedUtc, c.DurationSeconds, c.PhoneAccountId, c.Version,
                };

                string result;
                if (existing is null)
                {
                    await conn.ExecuteAsync(Cmd(@"
                        INSERT INTO SGAPP.MOBILE_CHIAMATE
                            (EVENT_UUID, DISPOSITIVO_ID, NUMERO_CHIAMANTE, PRESENTAZIONE, DIREZIONE, ESITO, TIPO_ORIGINALE,
                             DATA_INIZIO, DATA_FINE, DURATA_SECONDI, ACCOUNT_TELEFONICO, VERSIONE)
                        VALUES
                            (:EventUuid, :DeviceId, :PhoneNumber, :Presentation, :Direction, :Status, :RawType,
                             :StartedUtc, :EndedUtc, :DurationSeconds, :PhoneAccountId, :Version)", args, ct, tx));
                    result = CallResults.Created;
                }
                else if (existing.DispositivoId != deviceId)
                {
                    result = CallResults.Conflict;
                }
                else if (existing.Versione >= c.Version)
                {
                    result = existing.Versione == c.Version ? CallResults.Unchanged : CallResults.Stale;
                }
                else
                {
                    await conn.ExecuteAsync(Cmd(@"
                        UPDATE SGAPP.MOBILE_CHIAMATE
                           SET NUMERO_CHIAMANTE = :PhoneNumber, PRESENTAZIONE = :Presentation, DIREZIONE = :Direction,
                               ESITO = :Status, TIPO_ORIGINALE = :RawType, DATA_INIZIO = :StartedUtc, DATA_FINE = :EndedUtc,
                               DURATA_SECONDI = :DurationSeconds, ACCOUNT_TELEFONICO = :PhoneAccountId, VERSIONE = :Version,
                               UPDATED_AT = SYS_EXTRACT_UTC(SYSTIMESTAMP)
                         WHERE EVENT_UUID = :EventUuid AND DISPOSITIVO_ID = :DeviceId AND VERSIONE < :Version", args, ct, tx));
                    result = CallResults.Updated;
                }

                await tx.CommitAsync(ct);
                return result;
            }
            catch (OracleException ex) when (ex.Number == OraUniqueViolation && attempt == 0)
            {
                // Inserimento concorrente dello stesso EVENT_UUID: si rilegge e si decide di nuovo.
                await tx.RollbackAsync(CancellationToken.None);
            }
        }
    }

    // ------------------------------------------------------------------ consultazione (pagina admin)

    public async Task<List<MobileDeviceStats>> ListDevicesWithStatsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        var rows = await conn.QueryAsync<MobileDeviceStats>(Cmd(@"
            SELECT d.ID AS Id, d.DEVICE_UUID AS DeviceUuid, d.NOME AS Nome, d.PROPRIETARIO AS Proprietario,
                   d.NUMERO_SIM AS NumeroSim, d.MODELLO AS Modello, d.APP_VERSION AS AppVersion,
                   d.ANDROID_VERSION AS AndroidVersion, d.ATTIVO AS Attivo, d.ULTIMO_CONTATTO AS UltimoContatto,
                   d.CREATED_AT AS CreatedAt,
                   NVL(SUM(CASE WHEN c.DIREZIONE = 'INCOMING' AND c.ESITO = 'ANSWERED' THEN 1 END), 0) AS Risposte,
                   NVL(SUM(CASE WHEN c.ESITO = 'MISSED' THEN 1 END), 0) AS Perse,
                   NVL(SUM(CASE WHEN c.ESITO = 'REJECTED' THEN 1 END), 0) AS Rifiutate,
                   NVL(SUM(CASE WHEN c.DIREZIONE = 'OUTGOING' THEN 1 END), 0) AS Uscita
              FROM SGAPP.MOBILE_DISPOSITIVI d
              LEFT JOIN SGAPP.MOBILE_CHIAMATE c
                ON c.DISPOSITIVO_ID = d.ID AND c.DATA_INIZIO >= :FromUtc AND c.DATA_INIZIO < :ToUtc
             GROUP BY d.ID, d.DEVICE_UUID, d.NOME, d.PROPRIETARIO, d.NUMERO_SIM, d.MODELLO, d.APP_VERSION,
                      d.ANDROID_VERSION, d.ATTIVO, d.ULTIMO_CONTATTO, d.CREATED_AT
             ORDER BY d.ATTIVO DESC, d.NOME", new { FromUtc = fromUtc, ToUtc = toUtc }, ct));
        return rows.ToList();
    }

    public async Task<List<MobileCallRow>> SearchCallsAsync(CallSearch q, CancellationToken ct)
    {
        var number = MobileValidation.NormalizePhone(q.NumberContains);
        await using var conn = await OpenAsync(ct);
        var rows = await conn.QueryAsync<MobileCallRow>(Cmd(@"
            SELECT c.ID AS Id, c.DATA_INIZIO AS DataInizio, c.DURATA_SECONDI AS DurataSecondi,
                   c.NUMERO_CHIAMANTE AS NumeroChiamante, c.PRESENTAZIONE AS Presentazione,
                   c.DIREZIONE AS Direzione, c.ESITO AS Esito, c.DISPOSITIVO_ID AS DispositivoId,
                   d.NOME AS Nome, d.PROPRIETARIO AS Proprietario
              FROM SGAPP.MOBILE_CHIAMATE c
              JOIN SGAPP.MOBILE_DISPOSITIVI d ON d.ID = c.DISPOSITIVO_ID
             WHERE c.DATA_INIZIO >= :FromUtc AND c.DATA_INIZIO < :ToUtc
               AND (:DeviceId IS NULL OR c.DISPOSITIVO_ID = :DeviceId)
               AND (:Status IS NULL OR c.ESITO = :Status)
               AND (:Direction IS NULL OR c.DIREZIONE = :Direction)
               AND (:NumberLike IS NULL OR c.NUMERO_CHIAMANTE LIKE :NumberLike)
             ORDER BY c.DATA_INIZIO DESC
             FETCH FIRST :Limit ROWS ONLY",
            new
            {
                FromUtc = q.FromUtc, ToUtc = q.ToUtc, q.DeviceId, q.Status, q.Direction,
                NumberLike = number is null ? null : "%" + number.TrimStart('+') + "%",
                q.Limit,
            }, ct));
        return rows.ToList();
    }

    /// <summary>Crea un codice monouso e ne salva solo l'hash. Restituisce il codice in chiaro (mostrato una volta).</summary>
    public async Task<(string Code, DateTime ExpiresUtc)> CreateActivationAsync(
        string createdBy, string? description, long? deviceId, TimeSpan validity, CancellationToken ct)
    {
        var code = MobileTokens.NewActivationCode();
        var expires = DateTime.UtcNow.Add(validity);
        await using var conn = await OpenAsync(ct);
        await using var tx = (OracleTransaction)await conn.BeginTransactionAsync(ct);
        if (deviceId is { } id)
        {
            // Un solo codice di riassociazione valido per dispositivo.
            await conn.ExecuteAsync(Cmd(@"
                UPDATE SGAPP.MOBILE_ATTIVAZIONI SET REVOCATO = 1
                 WHERE DISPOSITIVO_ID = :Id AND USATO_AT IS NULL AND REVOCATO = 0", new { Id = id }, ct, tx));
        }
        await conn.ExecuteAsync(Cmd(@"
            INSERT INTO SGAPP.MOBILE_ATTIVAZIONI (CODICE_HASH, DESCRIZIONE, CREATO_DA, SCADENZA, DISPOSITIVO_ID)
            VALUES (:CodeHash, :Descrizione, :CreatoDa, :Scadenza, :DeviceId)",
            new
            {
                CodeHash = MobileTokens.Sha256Hex(MobileTokens.NormalizeActivationCode(code)),
                Descrizione = MobileValidation.Clean(description, 200),
                CreatoDa = MobileValidation.Clean(createdBy, 100) ?? "?",
                Scadenza = expires,
                DeviceId = deviceId,
            }, ct, tx));
        await tx.CommitAsync(ct);
        return (code, expires);
    }

    /// <summary>Disattivare un dispositivo blocca subito le sue richieste (token rifiutato con 403).</summary>
    public async Task SetDeviceActiveAsync(long deviceId, bool active, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await conn.ExecuteAsync(Cmd(@"
            UPDATE SGAPP.MOBILE_DISPOSITIVI
               SET ATTIVO = :Attivo, UPDATED_AT = SYS_EXTRACT_UTC(SYSTIMESTAMP)
             WHERE ID = :Id", new { Attivo = active ? 1 : 0, Id = deviceId }, ct));
    }
}
