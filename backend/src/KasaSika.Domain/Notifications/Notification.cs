namespace KasaSika.Domain.Notifications;

/// <summary>
/// Something the user should hear the next time they open the app, e.g. "your transfer to Ama succeeded"
/// when MTN's answer arrived after they left. The text is stored already localized in the user's language.
/// </summary>
public sealed class Notification
{
    private Notification() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid? TransactionId { get; private set; }
    public string Kind { get; private set; } = "";
    public string Language { get; private set; } = "";
    public string Text { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ReadAt { get; private set; }

    public static Notification Create(Guid userId, Guid? transactionId, string kind, string language, string text, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        TransactionId = transactionId,
        Kind = kind,
        Language = language,
        Text = text,
        CreatedAt = now,
    };

    public void MarkRead(DateTimeOffset now) => ReadAt ??= now;
}
