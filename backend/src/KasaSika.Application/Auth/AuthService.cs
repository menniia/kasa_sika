using KasaSika.Application.Abstractions;
using KasaSika.Application.Options;
using KasaSika.Application.Speech;
using KasaSika.Domain;
using KasaSika.Domain.Identity;
using KasaSika.Domain.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KasaSika.Application.Auth;

/// <param name="InstallationId">Random id the app generates on first launch and keeps in secure storage.</param>
public sealed record DeviceInfo(string InstallationId, string Platform, string? Label);

public sealed record RegisterRequest(string DisplayName, string PhoneNumber, string Passcode, DeviceInfo? Device);
public sealed record LoginRequest(string PhoneNumber, string Passcode, DeviceInfo? Device);
public sealed record PreferencesRequest(string? Language, decimal? SpeechRate, bool? HandsFree);

public sealed record PreferencesView(string Language, decimal SpeechRate, bool HandsFree);
public sealed record UserView(Guid Id, string DisplayName, string MaskedPhoneNumber, bool IsDemo, PreferencesView Preferences);

public sealed record SessionResult(string AccessToken, DateTimeOffset ExpiresAt, UserView User, Guid? DeviceId, IReadOnlyList<AuthenticationMethod> StepUpMethods);

/// <summary>
/// Registration, login and session handling. This is the *session* layer ("who is calling this API?").
/// It is separate from per-transfer step-up authentication, which is handled in TransactionService.
/// </summary>
public sealed class AuthService(
    IKasaDbContext db,
    IPasscodeHasher hasher,
    IFieldProtector protector,
    ITokenService tokens,
    ILanguageRegistry languages,
    IEnumerable<IStepUpAuthenticator> authenticators,
    TimeProvider clock,
    IOptions<AuthOptions> options,
    IOptions<DemoOptions> demo)
{
    private readonly AuthOptions _options = options.Value;

    public async Task<SessionResult> RegisterAsync(RegisterRequest request, string correlationId, CancellationToken ct)
    {
        if (!_options.AllowSelfRegistration)
            throw new KasaException(ErrorKind.Forbidden, "REGISTRATION_DISABLED", "Registration is not open on this server.");

        var name = (request.DisplayName ?? "").Trim();
        if (name.Length is < 1 or > 80)
            throw KasaException.Validation("INVALID_DISPLAY_NAME", "Please enter a name of up to 80 characters.");
        var msisdn = Msisdn.Parse(request.PhoneNumber);
        EnsurePasscodeAcceptable(request.Passcode);

        if (await db.Users.AnyAsync(u => u.Msisdn == msisdn.International, ct))
            throw KasaException.Conflict("ACCOUNT_EXISTS", "An account already exists for that phone number.");

        var now = clock.GetUtcNow();
        var user = User.Create(name, msisdn, hasher.Hash(request.Passcode), now);
        db.Users.Add(user);
        db.AuditEvents.Add(new AuditEvent(user.Id, null, "USER_REGISTERED", "OK", null, correlationId, now));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two registrations for the same number raced past the check above; the unique index caught it.
            throw KasaException.Conflict("ACCOUNT_EXISTS", "An account already exists for that phone number.");
        }
        return await StartSessionAsync(user, request.Device, correlationId, ct);
    }

    public async Task<SessionResult> LoginAsync(LoginRequest request, string correlationId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var invalid = KasaException.Unauthenticated("INVALID_CREDENTIALS", "The phone number or passcode is not correct.");

        if (!Msisdn.TryParse(request.PhoneNumber, out var msisdn) || string.IsNullOrEmpty(request.Passcode))
        {
            hasher.BurnTime(request.Passcode ?? "");
            throw invalid;
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Msisdn == msisdn!.International, ct);
        if (user is null)
        {
            // Same work and same answer as a wrong passcode, so the response does not reveal which numbers have accounts.
            hasher.BurnTime(request.Passcode);
            db.AuthenticationAttempts.Add(new AuthenticationAttempt(null, null, AuthenticationMethod.Login, AuthenticationResult.Failure, "Unknown phone number", now));
            await db.SaveChangesAsync(ct);
            throw invalid;
        }

        if (user.IsLocked(now))
        {
            db.AuthenticationAttempts.Add(new AuthenticationAttempt(user.Id, null, AuthenticationMethod.Login, AuthenticationResult.Locked, "Login attempted while locked", now));
            await db.SaveChangesAsync(ct);
            throw new KasaException(ErrorKind.Locked, "ACCOUNT_LOCKED", "Too many wrong attempts. Please try again later.");
        }

        var verification = hasher.Verify(user.PasscodeHash, request.Passcode);
        if (verification == PasscodeVerification.Failed)
        {
            user.RecordFailedAttempt(now, _options.MaxFailedAttempts, TimeSpan.FromMinutes(_options.LockoutMinutes));
            db.AuthenticationAttempts.Add(new AuthenticationAttempt(user.Id, null, AuthenticationMethod.Login, AuthenticationResult.Failure, "Wrong passcode", now));
            await db.SaveChangesAsync(ct);
            if (user.IsLocked(now))
                throw new KasaException(ErrorKind.Locked, "ACCOUNT_LOCKED", "Too many wrong attempts. Please try again later.");
            throw invalid;
        }

        if (verification == PasscodeVerification.SuccessRehashNeeded) user.ReplacePasscodeHash(hasher.Hash(request.Passcode));
        user.RecordSuccessfulAttempt();
        db.AuthenticationAttempts.Add(new AuthenticationAttempt(user.Id, null, AuthenticationMethod.Login, AuthenticationResult.Success, "Login", now));
        return await StartSessionAsync(user, request.Device, correlationId, ct);
    }

    /// <summary>
    /// "Continue with demo account" for judges and presenters. Only when demo mode is on (which the API refuses
    /// with a live provider). Every transfer still needs its own step-up verification.
    /// </summary>
    public async Task<SessionResult> DemoLoginAsync(DeviceInfo? device, string correlationId, CancellationToken ct)
    {
        if (!demo.Value.Enabled || !demo.Value.AllowPasswordlessDemoLogin)
            throw KasaException.Forbidden("DEMO_DISABLED", "Demo sign-in is not available on this server.");
        var msisdn = Msisdn.Parse(demo.Value.UserPhone);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Msisdn == msisdn.International && u.IsDemo, ct)
            ?? throw KasaException.NotFound("DEMO_USER_MISSING", "The demo account has not been created.");
        db.AuditEvents.Add(new AuditEvent(user.Id, null, "DEMO_LOGIN", "OK", null, correlationId, clock.GetUtcNow()));
        return await StartSessionAsync(user, device, correlationId, ct);
    }

    public async Task LogoutAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return;
        session.Revoke(clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Called on every authenticated request: the token is only valid while its session row is.</summary>
    public async Task<bool> IsSessionActiveAsync(Guid sessionId, Guid userId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return await db.Sessions.AnyAsync(s => s.Id == sessionId && s.UserId == userId && s.RevokedAt == null && s.ExpiresAt > now, ct);
    }

    public async Task<UserView> GetUserAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw KasaException.NotFound("USER_NOT_FOUND", "User not found.");
        return ToView(user);
    }

    public async Task<UserView> UpdatePreferencesAsync(Guid userId, PreferencesRequest request, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw KasaException.NotFound("USER_NOT_FOUND", "User not found.");
        var language = request.Language is null ? null : languages.Get(request.Language).Code;
        user.UpdatePreferences(language, request.SpeechRate, request.HandsFree);
        await db.SaveChangesAsync(ct);
        return ToView(user);
    }

    /// <summary>
    /// Enrols this phone's biometric-protected device key. Requires the passcode, so a stolen session token
    /// alone cannot register an attacker's device as a way to approve transfers.
    /// </summary>
    public async Task EnrollDeviceKeyAsync(Guid userId, Guid deviceId, string passcode, string deviceKeyBase64, string correlationId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw KasaException.Unauthenticated("UNAUTHENTICATED", "Please sign in again.");
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId && d.UserId == userId && d.RevokedAt == null, ct)
            ?? throw KasaException.NotFound("DEVICE_NOT_FOUND", "This device is not registered.");
        if (user.IsLocked(now))
            throw new KasaException(ErrorKind.Locked, "ACCOUNT_LOCKED", "Too many wrong attempts. Please try again later.");
        if (hasher.Verify(user.PasscodeHash, passcode ?? "") == PasscodeVerification.Failed)
        {
            user.RecordFailedAttempt(now, _options.MaxFailedAttempts, TimeSpan.FromMinutes(_options.LockoutMinutes));
            await db.SaveChangesAsync(ct);
            throw KasaException.Forbidden(ErrorCodes.AuthenticationFailed, "That passcode is not correct.");
        }

        byte[] key;
        try { key = Convert.FromBase64String(deviceKeyBase64); }
        catch (FormatException) { throw KasaException.Validation("INVALID_DEVICE_KEY", "The device key is not valid."); }
        if (key.Length != 32) throw KasaException.Validation("INVALID_DEVICE_KEY", "The device key must be 256 bits.");

        foreach (var old in db.DeviceCredentials.Where(c => c.DeviceId == device.Id && c.RevokedAt == null)) old.Revoke(now);
        db.DeviceCredentials.Add(DeviceCredential.Enroll(userId, device.Id, DeviceCredentialKind.BiometricDeviceKey, protector.Encrypt(deviceKeyBase64), now));
        db.AuditEvents.Add(new AuditEvent(userId, null, "DEVICE_KEY_ENROLLED", "OK", device.Platform, correlationId, now));
        user.RecordSuccessfulAttempt();
        await db.SaveChangesAsync(ct);
    }

    private async Task<SessionResult> StartSessionAsync(User user, DeviceInfo? deviceInfo, string correlationId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        Device? device = null;
        if (deviceInfo is { InstallationId.Length: >= 8 and <= 100 })
        {
            device = await db.Devices.FirstOrDefaultAsync(d => d.UserId == user.Id && d.InstallationId == deviceInfo.InstallationId && d.RevokedAt == null, ct);
            if (device is null)
            {
                device = Device.Register(user.Id, deviceInfo.InstallationId, deviceInfo.Platform ?? "unknown", deviceInfo.Label, now);
                db.Devices.Add(device);
                db.AuditEvents.Add(new AuditEvent(user.Id, null, "DEVICE_REGISTERED", "OK", device.Platform, correlationId, now));
            }
            else device.Seen(now);
        }

        var session = Session.Start(user.Id, device?.Id, deviceInfo?.Label, now, TimeSpan.FromHours(_options.SessionHours));
        db.Sessions.Add(session);
        db.AuditEvents.Add(new AuditEvent(user.Id, null, "SESSION_STARTED", "OK", null, correlationId, now));
        await db.SaveChangesAsync(ct);
        var token = tokens.Issue(user, session);
        var methods = authenticators.Select(a => a.Method).Where(m => _options.EffectiveStepUpMethods.Contains(m)).Distinct().ToList();
        return new SessionResult(token.Value, token.ExpiresAt, ToView(user), device?.Id, methods);
    }

    private static UserView ToView(User user) =>
        new(user.Id, user.DisplayName, Msisdn.FromStored(user.Msisdn).Masked, user.IsDemo,
            new PreferencesView(user.PreferredLanguage, user.SpeechRate, user.HandsFree));

    /// <summary>
    /// A 6-digit numeric passcode: quick to type with a screen reader and a numeric keypad. Rejects the
    /// obvious ones. This is a usability/security compromise for a prototype, not a recommendation.
    /// </summary>
    public static void EnsurePasscodeAcceptable(string? passcode)
    {
        if (passcode is null || passcode.Length != 6 || !passcode.All(char.IsAsciiDigit))
            throw KasaException.Validation("INVALID_PASSCODE", "The passcode must be exactly 6 digits.");
        var allSame = passcode.Distinct().Count() == 1;
        var sequential = "0123456789".Contains(passcode, StringComparison.Ordinal) || "9876543210".Contains(passcode, StringComparison.Ordinal);
        if (allSame || sequential)
            throw KasaException.Validation("WEAK_PASSCODE", "Please choose a passcode that is not repeated or sequential digits.");
    }
}
