using static System.Runtime.InteropServices.JavaScript.JSType;
namespace KasaSika.Domain.Identity;

/// <summary>
/// A login session. The JWT the app holds only carries this session's id; the server checks here on every
/// request, so a session can e revoked (logout, lost phone) without waiting for the token to expire
/// </summary>

public sealed class Session
{
    private Session() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string? DeviceLabel { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiredAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    public static Session Start(Guid userId, string? deviceLabel, DateTimeOffset now, TimeSpan lifetime) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        DeviceLabel = deviceLabel is { Length: > 100 } ? deviceLabel[..100] : deviceLabel,
        CreatedAt = now,
        ExpiredAt = now + lifetime,
    };

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiredAt > now;

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;

}