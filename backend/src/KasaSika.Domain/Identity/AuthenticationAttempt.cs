namespace KasaSika.Domain.Identity;

public enum AuthenticationMethod { Login, Passcode, DeviceBiometric, DemoSimulated }

public enum AuthenticationResult { Success, Failure, Locked }

/// <summary>
/// Every login / step-up attempt, successful or not. Append-only. For a step-up attempt it records exactly
/// what was being authenticated (transaction, amount, currency, masked recipient, device), so the audit
/// trail can show that "GHS 50 to Ama" was authorized and nothing else.
/// </summary>
public sealed class AuthenticationAttempt
{
    private AuthenticationAttempt() { }

    public AuthenticationAttempt(Guid? userId, Guid? transactionId, AuthenticationMethod method, AuthenticationResult result, string reason, DateTimeOffset at)
    {
        Id = Guid.NewGuid();
        UserId = userId;
        TransactionId = transactionId;
        Method = method;
        Result = result;
        Reason = reason;
        OccurredAt = at;
    }

    public Guid Id { get; private set; }

    /// <summary>Null when a login attempt for a number that has no account.</summary>
    public Guid? UserId { get; private set; }

    public Guid? TransactionId { get; private set; }
    public Guid? DeviceId { get; private set; }
    public Guid? ChallengeId { get; private set; }

    public long? AmountMinor { get; private set; }
    public string? Currency { get; private set; }
    public string? RecipientMasked { get; private set; }

    public AuthenticationMethod Method { get; private set; }

    public AuthenticationResult Result { get; private set; }

    public string Reason { get; private set; } = "";

    public DateTimeOffset OccurredAt { get; private set; }

    public AuthenticationAttempt ForTransfer(Guid? deviceId, Guid challengeId, long amountMinor, string currency, string recipientMasked)
    {
        DeviceId = deviceId;
        ChallengeId = challengeId;
        AmountMinor = amountMinor;
        Currency = currency;
        RecipientMasked = recipientMasked;
        return this;
    }
}
