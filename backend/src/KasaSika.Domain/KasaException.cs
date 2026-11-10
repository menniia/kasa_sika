namespace KasaSika.Domain;

public enum ErrorKind
{
    Validation,
    Unauthenticated,
    Forbidden,
    NotFound,
    Conflict,
    Gone,
    Locked,
    TooManyRequests,
    ProviderFailure, // 502 - the payment provider could not be reached / rejected the call
    NotSupported,
    ServiceUnavailable, // 503 - a dependency such as the speech provider is down
}

/// <summary>
/// Normalized, provider-independent error codes. The app turns these into plain-language speech; raw HTTP
/// statuses and exception names never reach the user.
/// </summary>
public static class ErrorCodes
{
    public const string RecipientNotFound = "RECIPIENT_NOT_FOUND";
    public const string InvalidAmount = "INVALID_AMOUNT";
    public const string AuthenticationFailed = "AUTHENTICATION_FAILED";
    public const string AuthenticationLocked = "AUTHENTICATION_LOCKED";
    public const string ConsentRequired = "CONSENT_REQUIRED";
    public const string ProviderUnavailable = "PROVIDER_UNAVAILABLE";
    public const string TransactionPending = "TRANSACTION_PENDING";
    public const string TransactionUnknown = "TRANSACTION_UNKNOWN";
    public const string DuplicateTransaction = "DUPLICATE_TRANSACTION";
    public const string SpeechUnavailable = "SPEECH_UNAVAILABLE";
}

public sealed class KasaException(ErrorKind kind, string code, string message, bool retryable = false) : Exception(message)
{
    public ErrorKind Kind { get; } = kind;
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;

    public static KasaException Validation(string code, string message) => new(ErrorKind.Validation, code, message);

    public static KasaException NotFound(string code, string message) => new(ErrorKind.NotFound, code, message);

    public static KasaException Conflict(string code, string message) => new(ErrorKind.Conflict, code, message);

    public static KasaException Unauthenticated(string code, string message) => new(ErrorKind.Unauthenticated, code, message);

    public static KasaException Forbidden(string code, string message) => new(ErrorKind.Forbidden, code, message);
}
