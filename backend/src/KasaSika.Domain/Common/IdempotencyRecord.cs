namespace KasaSika.Domain.Common;

/// <summary>
/// "This request was already handled; here is the answer we gave." Used for operations that are not the
/// creation of a transaction (which has its own unique key), chiefly conversation turns: a voice command
/// re-sent after a dropped connection, or a double tap, returns the stored reply instead of acting twice.
/// </summary>
public sealed class IdempotencyRecord
{
    private IdempotencyRecord() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string Scope { get; private set; } = "";
    public string Key { get; private set; } = "";
    public string RequestFingerprint { get; private set; } = "";
    public string ResponseJson { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }

    public static IdempotencyRecord Create(Guid userId, string scope, string key, string requestFingerprint, string responseJson, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Scope = scope,
        Key = key,
        RequestFingerprint = requestFingerprint,
        ResponseJson = responseJson,
        CreatedAt = now,
    };
}
