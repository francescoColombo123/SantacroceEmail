using Microsoft.AspNetCore.Mvc;
using Oracle.ManagedDataAccess.Client;

namespace SCemail.Mobile;

/// <summary>
/// API per l'app Android "Call Registry". Contratto e codici HTTP: Mobile/README.md.
/// I numeri di telefono non vengono mai scritti nei log.
/// </summary>
[ApiController]
[Route("api/mobile")]
[Produces("application/json")]
public sealed class MobileController : ControllerBase
{
    private readonly MobileRepository _repo;
    private readonly ILogger<MobileController> _log;

    public MobileController(MobileRepository repo, ILogger<MobileController> log)
    {
        _repo = repo;
        _log = log;
    }

    /// <summary>Stato del servizio. Con il token del dispositivo indica anche se è ancora attivo.</summary>
    [HttpGet("health")]
    public async Task<IActionResult> Health(CancellationToken ct)
    {
        var dbOk = await _repo.PingAsync(ct);
        bool? deviceActive = null;
        var token = MobileDeviceAuthFilter.ReadBearerToken(Request);
        if (dbOk && token is not null)
        {
            var device = await _repo.FindDeviceByTokenHashAsync(MobileTokens.Sha256Hex(token), ct);
            if (device is null)
                return Unauthorized(new ErrorResponse("unauthorized", "Token del dispositivo non valido"));
            deviceActive = device.IsActive;
        }

        var body = new HealthResponse(dbOk ? "ok" : "db_unavailable", dbOk, DateTime.UtcNow, deviceActive);
        return dbOk ? Ok(body) : StatusCode(StatusCodes.Status503ServiceUnavailable, body);
    }

    /// <summary>Registra il telefono consumando un codice di attivazione monouso.</summary>
    [HttpPost("devices/register")]
    public async Task<IActionResult> Register([FromBody] RegisterDeviceRequest? request, CancellationToken ct)
    {
        if (request is null) return BadRequest(new ErrorResponse("invalid_request", "Corpo della richiesta mancante"));
        var error = MobileValidation.ValidateRegistration(request);
        if (error is not null) return BadRequest(new ErrorResponse("invalid_request", error));

        var token = MobileTokens.NewDeviceToken();
        try
        {
            var (outcome, deviceId) = await _repo.RegisterAsync(request, MobileTokens.Sha256Hex(token), ct);
            switch (outcome)
            {
                case RegisterOutcome.Registered:
                    _log.LogInformation("API mobile: dispositivo {DeviceId} registrato", deviceId);
                    return StatusCode(StatusCodes.Status201Created, new RegisterDeviceResponse(
                        deviceId, token, request.DeviceName!.Trim(), request.OwnerName!.Trim(), DateTime.UtcNow));
                case RegisterOutcome.InvalidCode:
                    _log.LogWarning("API mobile: codice di attivazione non valido da {Ip}", HttpContext.Connection.RemoteIpAddress);
                    return StatusCode(StatusCodes.Status403Forbidden,
                        new ErrorResponse("activation_invalid", "Codice di attivazione non valido, scaduto o già usato"));
                default:
                    return Conflict(new ErrorResponse("device_conflict", "Identificativo dispositivo già associato a un altro telefono"));
            }
        }
        catch (OracleException ex)
        {
            _log.LogError(ex, "API mobile: errore DB in registrazione");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ErrorResponse("db_unavailable", "Database non disponibile, riprovare"));
        }
    }

    /// <summary>
    /// Invio di una o più chiamate (max 100). Idempotente su eventUuid: inviare di nuovo
    /// lo stesso evento non crea duplicati.
    /// </summary>
    [HttpPost("calls")]
    [MobileDeviceAuth]
    public async Task<IActionResult> PostCalls([FromBody] CallBatchRequest? request, CancellationToken ct)
    {
        if (request?.Calls is null || request.Calls.Count == 0)
            return BadRequest(new ErrorResponse("invalid_request", "Nessuna chiamata nella richiesta"));
        if (request.Calls.Count > MobileValidation.MaxBatchSize)
            return BadRequest(new ErrorResponse("batch_too_large", $"Massimo {MobileValidation.MaxBatchSize} chiamate per richiesta"));

        var device = (MobileDevice)HttpContext.Items[MobileDeviceAuthFilter.DeviceItemKey]!;
        if (!string.Equals(request.DeviceUuid, device.DeviceUuid, StringComparison.OrdinalIgnoreCase))
            return StatusCode(StatusCodes.Status403Forbidden,
                new ErrorResponse("device_mismatch", "deviceUuid non corrisponde al dispositivo autenticato"));

        var now = DateTime.UtcNow;
        var results = new List<CallResultDto>(request.Calls.Count);
        try
        {
            foreach (var dto in request.Calls)
            {
                var (call, error) = MobileValidation.ValidateCall(dto, now);
                if (call is null)
                {
                    results.Add(new CallResultDto(dto.EventUuid ?? "", CallResults.Invalid, error));
                    continue;
                }
                var result = await _repo.UpsertCallAsync(device.Id, call, ct);
                results.Add(new CallResultDto(call.EventUuid, result,
                    result == CallResults.Conflict ? "eventUuid appartiene a un altro dispositivo" : null));
            }
        }
        catch (OracleException ex)
        {
            // Gli eventi già salvati restano: al nuovo invio risulteranno "unchanged".
            _log.LogError(ex, "API mobile: errore DB salvando chiamate del dispositivo {DeviceId}", device.Id);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ErrorResponse("db_unavailable", "Database non disponibile, riprovare"));
        }

        _log.LogInformation("API mobile: dispositivo {DeviceId}, {Count} chiamate ({Summary})", device.Id, results.Count,
            string.Join(", ", results.GroupBy(r => r.Result).Select(g => $"{g.Key}={g.Count()}")));
        return Ok(new CallBatchResponse(results, now));
    }
}
