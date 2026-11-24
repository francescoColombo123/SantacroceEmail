using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace SCemail.Mobile;

/// <summary>
/// Richiede "Authorization: Bearer &lt;deviceToken&gt;". Il dispositivo è identificato dal
/// token (confrontato tramite hash), mai dal deviceUuid dichiarato nel JSON.
/// </summary>
public sealed class MobileDeviceAuthAttribute : TypeFilterAttribute
{
    public MobileDeviceAuthAttribute() : base(typeof(MobileDeviceAuthFilter)) { }
}

public sealed class MobileDeviceAuthFilter : IAsyncActionFilter
{
    public const string DeviceItemKey = "SCemail.Mobile.Device";

    private readonly MobileRepository _repo;
    private readonly ILogger<MobileDeviceAuthFilter> _log;

    public MobileDeviceAuthFilter(MobileRepository repo, ILogger<MobileDeviceAuthFilter> log)
    {
        _repo = repo;
        _log = log;
    }

    public static string? ReadBearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && header.Length > prefix.Length
            ? header[prefix.Length..].Trim()
            : null;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var ct = context.HttpContext.RequestAborted;
        var token = ReadBearerToken(context.HttpContext.Request);
        if (token is null)
        {
            context.Result = new ObjectResult(new ErrorResponse("unauthorized", "Token del dispositivo mancante"))
                { StatusCode = StatusCodes.Status401Unauthorized };
            return;
        }

        var device = await _repo.FindDeviceByTokenHashAsync(MobileTokens.Sha256Hex(token), ct);
        if (device is null)
        {
            _log.LogWarning("API mobile: token non riconosciuto da {Ip}", context.HttpContext.Connection.RemoteIpAddress);
            context.Result = new ObjectResult(new ErrorResponse("unauthorized", "Token del dispositivo non valido"))
                { StatusCode = StatusCodes.Status401Unauthorized };
            return;
        }

        if (!device.IsActive)
        {
            context.Result = new ObjectResult(new ErrorResponse("device_disabled", "Dispositivo disattivato dall'amministratore"))
                { StatusCode = StatusCodes.Status403Forbidden };
            return;
        }

        context.HttpContext.Items[DeviceItemKey] = device;
        await _repo.TouchDeviceAsync(device.Id, ct);
        await next();
    }
}
