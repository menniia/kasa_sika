namespace KasaSika.Domain.Identity;

/// <summary>
/// A login session. The JWT the app holds only carries this session's id; the server checks here on every
/// request, so a session can be revoked (logout, lost phone) without waiting for the token to expire
/// </summary>
public sealed class Session
{
    private Session() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid? DeviceId { get; private set; }
    public string? DeviceLabel { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    public static Session Start(Guid userId, Guid? deviceId, string? deviceLabel, DateTimeOffset now, TimeSpan lifetime) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        DeviceId = deviceId,
        DeviceLabel = deviceLabel is { Length: > 100 } ? deviceLabel[..100] : deviceLabel,
        CreatedAt = now,
        ExpiresAt = now + lifetime,
    };

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}
