using System.Text;

namespace SCemail.Mobile;

/// <summary>Validazione e normalizzazione degli input delle API mobile (logica pura, senza DB).</summary>
public static class MobileValidation
{
    public const int MaxBatchSize = 100;
    public const long MaxDurationSeconds = 48 * 3600;
    private static readonly DateTime MinCallDateUtc = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly HashSet<string> Directions = new() { "INCOMING", "OUTGOING", "UNKNOWN" };
    private static readonly HashSet<string> Statuses = new() { "ANSWERED", "MISSED", "REJECTED", "UNKNOWN" };

    /// <summary>Presentazione del numero: valore API → valore salvato in MOBILE_CHIAMATE.PRESENTAZIONE.</summary>
    private static readonly Dictionary<string, string> Presentations = new()
    {
        ["ALLOWED"] = "VISIBILE",
        ["RESTRICTED"] = "PRIVATO",
        ["UNKNOWN"] = "SCONOSCIUTO",
        ["PAYPHONE"] = "PUBBLICO",
    };

    public static bool IsUuid(string? value) =>
        value is { Length: 36 } && Guid.TryParse(value, out _);

    /// <summary>Mantiene solo + iniziale, cifre, * e #. Restituisce null se vuoto.</summary>
    public static string? NormalizePhone(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw.Trim())
        {
            if (char.IsAsciiDigit(ch) || ch is '*' or '#') sb.Append(ch);
            else if (ch == '+' && sb.Length == 0) sb.Append(ch);
        }
        var result = sb.ToString();
        return result.Length == 0 || result == "+" ? null : result[..Math.Min(result.Length, 40)];
    }

    public static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var t = value.Trim();
        return t.Length <= maxLength ? t : t[..maxLength];
    }

    /// <summary>Restituisce un messaggio d'errore o null se la richiesta è valida.</summary>
    public static string? ValidateRegistration(RegisterDeviceRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.ActivationCode)) return "activationCode obbligatorio";
        if (!IsUuid(r.DeviceUuid)) return "deviceUuid deve essere un UUID";
        if (Clean(r.DeviceName, 100) is null) return "deviceName obbligatorio";
        if (Clean(r.OwnerName, 100) is null) return "ownerName obbligatorio";
        return null;
    }

    /// <summary>Valida una chiamata e restituisce i valori normalizzati da salvare.</summary>
    public static (NormalizedCall? Call, string? Error) ValidateCall(CallEventDto c, DateTime nowUtc)
    {
        if (!IsUuid(c.EventUuid)) return (null, "eventUuid deve essere un UUID");
        if (c.Version < 1) return (null, "version deve essere >= 1");

        var direction = c.Direction?.Trim().ToUpperInvariant();
        if (direction is null || !Directions.Contains(direction)) return (null, "direction non valida");

        var status = c.Status?.Trim().ToUpperInvariant();
        if (status is null || !Statuses.Contains(status)) return (null, "status non valido");

        var presentationKey = c.Presentation?.Trim().ToUpperInvariant() ?? "UNKNOWN";
        if (!Presentations.TryGetValue(presentationKey, out var presentation)) return (null, "presentation non valida");

        if (c.DurationSeconds < 0 || c.DurationSeconds > MaxDurationSeconds) return (null, "durationSeconds fuori intervallo");

        var started = c.StartedAt.UtcDateTime;
        if (started < MinCallDateUtc || started > nowUtc.AddDays(1)) return (null, "startedAt fuori intervallo");

        DateTime? ended = c.EndedAt?.UtcDateTime;
        if (ended is { } e && (e < started || e > started.AddSeconds(MaxDurationSeconds + 3600)))
            return (null, "endedAt incoerente con startedAt");

        // Il numero è salvato solo se la presentazione lo consente.
        var number = presentation == "VISIBILE" ? NormalizePhone(c.PhoneNumber) : null;
        if (presentation == "VISIBILE" && number is null) presentation = "SCONOSCIUTO";

        return (new NormalizedCall(
            EventUuid: c.EventUuid!.ToLowerInvariant(),
            Version: c.Version,
            PhoneNumber: number,
            Presentation: presentation,
            Direction: direction,
            Status: status,
            RawType: c.RawType is >= 0 and <= 999 ? c.RawType : null,
            StartedUtc: started,
            EndedUtc: ended,
            DurationSeconds: c.DurationSeconds,
            PhoneAccountId: Clean(c.PhoneAccountId, 100)), null);
    }
}

public sealed record NormalizedCall(
    string EventUuid,
    int Version,
    string? PhoneNumber,
    string Presentation,
    string Direction,
    string Status,
    int? RawType,
    DateTime StartedUtc,
    DateTime? EndedUtc,
    long DurationSeconds,
    string? PhoneAccountId);
