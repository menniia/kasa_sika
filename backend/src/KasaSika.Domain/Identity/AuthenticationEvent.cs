namespace KasaSika.Domain.Identity;

public enum AuthenticationMethod { Login, Passcode, DemoSimulated }

public enum AuthenticationResult { Success, Failure, Locked }

/// <summary>Every login / step-up attempt, successful or not. Append-only.</summary>
public sealed class AuthenticationEvent
{
    private AuthenticationEvent() { }

    public AuthenticationEvent(Guid? userId, Guid? transactionId, AuthenticationMethod method, AuthenticationResult result, string reason, DateTimeOffset at)
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

    public AuthenticationMethod Method { get; private set; }

    public AuthenticationResult Result { get; private set; }

    public string Reason { get; private set; } = "";

    public DateTimeOffset OccurredAt { get; private set; }
}
