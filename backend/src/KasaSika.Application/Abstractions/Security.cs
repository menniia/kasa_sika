using System.Security.Cryptography;
using System.Text;
using KasaSika.Domain.Identity;
using KasaSika.Domain.Transactions;
using Microsoft.EntityFrameworkCore;

namespace KasaSika.Application.Abstractions;

public enum PasscodeVerification { Failed, Success, SuccessRehashNeeded }

public interface IPasscodeHasher
{
    string Hash(string passcode);
    PasscodeVerification Verify(string hash, string passcode);
    /// <summary>Spends about as long as a real verification, so "no such user" is not faster than "wrong passcode".</summary>
    void BurnTime(string passcode);
}

/// <summary>Encryption at rest for personal data, plus keyed fingerprints (see the implementation for why keyed).</summary>
public interface IFieldProtector
{
    string Encrypt(string plaintext);
    string Decrypt(string protectedValue);
    /// <summary>HMAC-SHA256 with a server-side secret. Unlike a plain hash, cannot be brute-forced offline from a leaked database.</summary>
    string Fingerprint(string data);
}

public sealed record IssuedToken(string Value, DateTimeOffset ExpiresAt);

public interface ITokenService
{
    IssuedToken Issue(User user, Session session);
}

/// <summary>What the user sends to prove it is them, for one challenge.</summary>
/// <param name="Signature">Base64url HMAC-SHA256 over <see cref="DeviceProof.Message"/>, for device-biometric proofs.</param>
public sealed record StepUpProof(Guid ChallengeId, AuthenticationMethod Method, string? Passcode, Guid? DeviceId, string? Signature);

/// <summary>Everything an authenticator may look at. The transaction and challenge are already bound and checked.</summary>
public sealed record StepUpContext(User User, Transaction Transaction, AuthenticationChallenge Challenge);

/// <summary>
/// "Prove it is really you, for THIS transfer." Separated behind an interface on purpose: the prototype
/// implementations below are simple, and a production voice-biometrics / device-attestation implementation
/// can replace them without touching the transaction service.
///
/// Speech recognition is NOT authentication. What the user said is only ever used to build a transfer
/// request; nothing they say aloud is accepted as proof of identity.
/// </summary>
public interface IStepUpAuthenticator
{
    AuthenticationMethod Method { get; }
    Task<bool> VerifyAsync(StepUpContext context, StepUpProof proof, CancellationToken cancellationToken);
}

/// <summary>Default. The user types their Kasa Sika passcode on an accessible keypad (never spoken aloud).</summary>
public sealed class PasscodeStepUpAuthenticator(IPasscodeHasher hasher) : IStepUpAuthenticator
{
    public AuthenticationMethod Method => AuthenticationMethod.Passcode;

    public Task<bool> VerifyAsync(StepUpContext context, StepUpProof proof, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(proof.Passcode))
        {
            hasher.BurnTime("000000");
            return Task.FromResult(false);
        }
        return Task.FromResult(hasher.Verify(context.User.PasscodeHash, proof.Passcode) != PasscodeVerification.Failed);
    }
}

/// <summary>
/// The exact text a phone signs for a device-biometric proof. It contains what the user was told they are
/// approving (amount, currency, masked recipient), so a signature for one transfer cannot approve another.
/// </summary>
public static class DeviceProof
{
    public static string Message(AuthenticationChallenge challenge, Transaction tx) =>
        $"kasa-sika-v1|{challenge.Id:D}|{challenge.Nonce}|{tx.Id:D}|{tx.AmountMinor}|{tx.Currency}|{tx.RecipientMasked}";

    public static string Sign(byte[] key, string message) =>
        Base64Url(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(message)));

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// The phone's OS released an enrolled device key only after the owner's fingerprint/face, and used it to sign
/// this challenge. PROTOTYPE: the key is a shared secret (encrypted at rest on the server). Production should
/// use a non-exportable asymmetric key (Android Keystore / Secure Enclave / passkey) so the server holds only
/// a public key.
/// </summary>
public sealed class DeviceBiometricStepUpAuthenticator(IKasaDbContext db, IFieldProtector protector) : IStepUpAuthenticator
{
    public AuthenticationMethod Method => AuthenticationMethod.DeviceBiometric;

    public async Task<bool> VerifyAsync(StepUpContext context, StepUpProof proof, CancellationToken cancellationToken)
    {
        if (proof.DeviceId is not { } deviceId || string.IsNullOrEmpty(proof.Signature)) return false;
        // The challenge was issued to one device; a proof from another device is refused.
        if (context.Challenge.DeviceId is { } issuedTo && issuedTo != deviceId) return false;

        var secret = await db.DeviceCredentials
            .Where(c => c.UserId == context.User.Id && c.DeviceId == deviceId && c.Kind == DeviceCredentialKind.BiometricDeviceKey && c.RevokedAt == null)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => c.SecretProtected)
            .FirstOrDefaultAsync(cancellationToken);
        if (secret is null) return false;

        var key = Convert.FromBase64String(protector.Decrypt(secret));
        var expected = DeviceProof.Sign(key, DeviceProof.Message(context.Challenge, context.Transaction));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(proof.Signature));
    }
}

/// <summary>
/// PROTOTYPE ONLY. One tap, no credential, so a demo can be shown without typing. It proves nothing about the
/// user. Only registered when explicitly enabled AND the payment provider is the mock or an MTN *sandbox*;
/// the API refuses to start otherwise (see StartupValidation).
/// </summary>
public sealed class DemoStepUpAuthenticator : IStepUpAuthenticator
{
    public AuthenticationMethod Method => AuthenticationMethod.DemoSimulated;
    public Task<bool> VerifyAsync(StepUpContext context, StepUpProof proof, CancellationToken cancellationToken) => Task.FromResult(true);
}
