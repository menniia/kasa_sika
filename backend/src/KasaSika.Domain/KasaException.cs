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
}

public sealed class KasaException(ErrorKind kind, string code, string message, bool retryable = false) : Exception(message)
{
    public ErrorKind kind { get; } = kind;
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;

    public static KasaException Validation(string code, string message) => new (ErrorKind.Validation, code, message);

    public static KasaException NotFund(string code, string message) => new(ErrorKind.NotFound, code, message);

    public static KasaException Conflict(string code, string message) => new(ErrorKind.Conflict, code, message);

    public static KasaException Unauthenticated(string code, string message) => new(ErrorKind.Unauthenticated, code, message);
}