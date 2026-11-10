using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using KasaSika.Application.Abstractions;
using KasaSika.Domain.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace KasaSika.Infrastructure.Security;

/// <summary>
/// Server-side secrets. None of these have defaults: they must come from user-secrets / environment
/// variables, and the API refuses to start without them (see StartupValidation).
/// </summary>
public sealed class SecurityOptions
{
    public const string Section = "Security";

    /// <summary>Base64, at least 32 bytes. Signs session JWTs.</summary>
    public string JwtSigningKey { get; set; } = "";
    /// <summary>Base64, exactly 32 bytes. AES-256-GCM key for personal data at rest.</summary>
    public string FieldEncryptionKey { get; set; } = "";
    /// <summary>Base64, at least 32 bytes. HMAC key for request/authorization fingerprints.</summary>
    public string FingerprintKey { get; set; } = "";
    public string JwtIssuer { get; set; } = "kasa-sika";
    public string JwtAudience { get; set; } = "kasa-sika-app";
}

/// <summary>PBKDF2 (ASP.NET Core Identity v3 format). The work factor is what makes a stolen hash expensive to crack.</summary>
public sealed class Pbkdf2PasscodeHasher : IPasscodeHasher
{
    private static readonly PasswordHasher<object> Hasher = new(Microsoft.Extensions.Options.Options.Create(new PasswordHasherOptions { IterationCount = 210_000 }));
    private static readonly object Subject = new();
    private static readonly string DummyHash = Hasher.HashPassword(Subject, "not-a-real-passcode");

    public string Hash(string passcode) => Hasher.HashPassword(Subject, passcode);

    public PasscodeVerification Verify(string hash, string passcode) => Hasher.VerifyHashedPassword(Subject, hash, passcode) switch
    {
        PasswordVerificationResult.Success => PasscodeVerification.Success,
        PasswordVerificationResult.SuccessRehashNeeded => PasscodeVerification.SuccessRehashNeeded,
        _ => PasscodeVerification.Failed,
    };

    public void BurnTime(string passcode) => Hasher.VerifyHashedPassword(Subject, DummyHash, passcode);
}

/// <summary>
/// AES-256-GCM for personal data at rest ("v1:" + base64(nonce | ciphertext | tag)), and HMAC-SHA256
/// fingerprints. A fingerprint is keyed so that a leaked database alone cannot be used to confirm guesses
/// ("was 0244123456 a recipient?") by hashing candidate numbers.
/// </summary>
public sealed class AesGcmFieldProtector : IFieldProtector
{
    private const string Prefix = "v1:";
    private readonly byte[] _encryptionKey;
    private readonly byte[] _fingerprintKey;

    public AesGcmFieldProtector(IOptions<SecurityOptions> options)
    {
        _encryptionKey = Convert.FromBase64String(options.Value.FieldEncryptionKey);
        _fingerprintKey = Convert.FromBase64String(options.Value.FingerprintKey);
        if (_encryptionKey.Length != 32) throw new InvalidOperationException("Security:FieldEncryptionKey must be 32 bytes (base64).");
        if (_fingerprintKey.Length < 32) throw new InvalidOperationException("Security:FingerprintKey must be at least 32 bytes (base64).");
    }

    public string Encrypt(string plaintext)
    {
        var data = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var cipher = new byte[data.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        using var aes = new AesGcm(_encryptionKey, tag.Length);
        aes.Encrypt(nonce, data, cipher, tag);
        return Prefix + Convert.ToBase64String([.. nonce, .. cipher, .. tag]);
    }

    public string Decrypt(string protectedValue)
    {
        if (!protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
            throw new CryptographicException("Unrecognised protected value format.");
        var all = Convert.FromBase64String(protectedValue[Prefix.Length..]);
        var nonceSize = AesGcm.NonceByteSizes.MaxSize;
        var tagSize = AesGcm.TagByteSizes.MaxSize;
        var nonce = all[..nonceSize];
        var tag = all[^tagSize..];
        var cipher = all[nonceSize..^tagSize];
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_encryptionKey, tagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    public string Fingerprint(string data) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(_fingerprintKey, Encoding.UTF8.GetBytes(data)));
}

/// <summary>Short-lived JWT carrying only the user id and session id. The session row is checked on every request.</summary>
public sealed class JwtTokenService(IOptions<SecurityOptions> options, TimeProvider clock) : ITokenService
{
    public const string SessionClaim = "sid";
    public const string DeviceClaim = "did";

    public IssuedToken Issue(User user, Session session)
    {
        var o = options.Value;
        var key = new SymmetricSecurityKey(Convert.FromBase64String(o.JwtSigningKey));
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = o.JwtIssuer,
            Audience = o.JwtAudience,
            Subject = new ClaimsIdentity([
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(SessionClaim, session.Id.ToString()),
                .. session.DeviceId is { } deviceId ? [new Claim(DeviceClaim, deviceId.ToString())] : Array.Empty<Claim>(),
            ]),
            IssuedAt = clock.GetUtcNow().UtcDateTime,
            NotBefore = clock.GetUtcNow().UtcDateTime,
            Expires = session.ExpiresAt.UtcDateTime,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        };
        return new IssuedToken(new JsonWebTokenHandler().CreateToken(descriptor), session.ExpiresAt);
    }
}
