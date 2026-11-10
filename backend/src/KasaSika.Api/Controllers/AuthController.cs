using KasaSika.Api.Infrastructure;
using KasaSika.Application.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace KasaSika.Api.Controllers;

public sealed record EnrollDeviceKeyRequest(string Passcode, string DeviceKey);
public sealed record DemoLoginRequest(DeviceInfo? Device);

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController(AuthService auth) : ControllerBase
{
    [HttpPost("register"), AllowAnonymous, EnableRateLimiting("auth")]
    public Task<SessionResult> Register(RegisterRequest request, CancellationToken ct) =>
        auth.RegisterAsync(request, HttpContext.CorrelationId(), ct);

    [HttpPost("login"), AllowAnonymous, EnableRateLimiting("auth")]
    public Task<SessionResult> Login(LoginRequest request, CancellationToken ct) =>
        auth.LoginAsync(request, HttpContext.CorrelationId(), ct);

    /// <summary>Demo account sign-in (only when Demo:Enabled; refused with a live provider).</summary>
    [HttpPost("demo-login"), AllowAnonymous, EnableRateLimiting("auth")]
    public Task<SessionResult> DemoLogin(DemoLoginRequest? request, CancellationToken ct) =>
        auth.DemoLoginAsync(request?.Device, HttpContext.CorrelationId(), ct);

    [HttpPost("logout"), Authorize]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        await auth.LogoutAsync(User.SessionId(), ct);
        return NoContent();
    }

    [HttpGet("me"), Authorize]
    public Task<UserView> Me(CancellationToken ct) => auth.GetUserAsync(User.UserId(), ct);

    [HttpPut("me/preferences"), Authorize]
    public Task<UserView> Preferences(PreferencesRequest request, CancellationToken ct) =>
        auth.UpdatePreferencesAsync(User.UserId(), request, ct);

    /// <summary>Enrol this phone's biometric-protected device key (requires the passcode).</summary>
    [HttpPost("device-key"), Authorize, EnableRateLimiting("auth")]
    public async Task<IActionResult> EnrollDeviceKey(EnrollDeviceKeyRequest request, CancellationToken ct)
    {
        var deviceId = User.DeviceId() ?? throw Domain.KasaException.Validation("DEVICE_REQUIRED", "Sign in from the app on this device first.");
        await auth.EnrollDeviceKeyAsync(User.UserId(), deviceId, request.Passcode, request.DeviceKey, HttpContext.CorrelationId(), ct);
        return NoContent();
    }
}
