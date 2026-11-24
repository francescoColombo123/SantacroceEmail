namespace SCemail.Mobile;

// =============================================================================
//  Contratto JSON delle API /api/mobile/* (camelCase). Vedi Mobile/README.md.
// =============================================================================

/// <summary>Richiesta di registrazione di un telefono con codice di attivazione monouso.</summary>
public sealed class RegisterDeviceRequest
{
    public string? ActivationCode { get; set; }
    public string? DeviceUuid { get; set; }
    public string? DeviceName { get; set; }
    public string? OwnerName { get; set; }
    public string? SimNumber { get; set; }
    public string? AppVersion { get; set; }
    public string? AndroidVersion { get; set; }
    public string? Model { get; set; }
}

public sealed record RegisterDeviceResponse(
    long DeviceId,
    string DeviceToken,
    string DeviceName,
    string OwnerName,
    DateTime ServerTimeUtc);

/// <summary>Una chiamata rilevata dal telefono.</summary>
public sealed class CallEventDto
{
    /// <summary>UUID generato dal telefono: chiave di idempotenza.</summary>
    public string? EventUuid { get; set; }
    /// <summary>Versione progressiva dell'evento (>= 1).</summary>
    public int Version { get; set; }
    public string? PhoneNumber { get; set; }
    /// <summary>ALLOWED, RESTRICTED, UNKNOWN, PAYPHONE.</summary>
    public string? Presentation { get; set; }
    /// <summary>INCOMING, OUTGOING, UNKNOWN.</summary>
    public string? Direction { get; set; }
    /// <summary>ANSWERED, MISSED, REJECTED, UNKNOWN.</summary>
    public string? Status { get; set; }
    /// <summary>Valore grezzo di CallLog.Calls.TYPE (diagnostica).</summary>
    public int? RawType { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public long DurationSeconds { get; set; }
    public string? PhoneAccountId { get; set; }
}

public sealed class CallBatchRequest
{
    public string? DeviceUuid { get; set; }
    public List<CallEventDto>? Calls { get; set; }
}

/// <summary>
/// Esito per singola chiamata:
/// created / updated / unchanged / stale → il telefono la considera sincronizzata;
/// invalid / conflict → errore definitivo, non va ritentata.
/// </summary>
public sealed record CallResultDto(string EventUuid, string Result, string? Error = null);

public sealed record CallBatchResponse(IReadOnlyList<CallResultDto> Results, DateTime ServerTimeUtc);

public sealed record HealthResponse(string Status, bool Database, DateTime ServerTimeUtc, bool? DeviceActive);

public sealed record ErrorResponse(string Error, string Message);

public static class CallResults
{
    public const string Created = "created";
    public const string Updated = "updated";
    public const string Unchanged = "unchanged";
    public const string Stale = "stale";
    public const string Invalid = "invalid";
    public const string Conflict = "conflict";
}
