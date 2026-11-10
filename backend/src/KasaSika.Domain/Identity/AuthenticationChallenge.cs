namespace KasaSika.Domain.Identity;

/// <summary>
/// A single-use, short-lived challenge issued when a user confirms a specific transfer. Its binding
/// fingerprint covers user, transaction, amount, currency and recipient, so a proof produced for
/// "GHS 50 to Ama" cannot be replayed for any other transfer, or twice for the same one.
/// </summary>
public sealed class AuthenticationChallenge
{
    private AuthenticationChallenge() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid TransactionId { get; private set; }
    public Guid? DeviceId { get; private set; }
    /// <summary>Random, base64url. Signed by the device key for biometric proofs.</summary>
    public string Nonce { get; private set; } = "";
    /// <summary>Keyed hash of user|transaction|amount|currency|recipient at the moment of confirmation.</summary>
    public string BindingFingerprint { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? ConsumedAt { get; private set; }
    public int FailedAttempts { get; private set; }

    public static AuthenticationChallenge Issue(Guid userId, Guid transactionId, Guid? deviceId, string nonce, string bindingFingerprint, DateTimeOffset now, TimeSpan lifetime) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        TransactionId = transactionId,
        DeviceId = deviceId,
        Nonce = nonce,
        BindingFingerprint = bindingFingerprint,
        CreatedAt = now,
        ExpiresAt = now + lifetime,
    };

    public bool IsUsable(DateTimeOffset now) => ConsumedAt is null && now < ExpiresAt;

    public void RecordFailure() => FailedAttempts++;

    public void Consume(DateTimeOffset now)
    {
        if (ConsumedAt is not null)
            throw KasaException.Conflict("CHALLENGE_ALREADY_USED", "This verification was already used. Please start again.");
        ConsumedAt = now;
    }
}
