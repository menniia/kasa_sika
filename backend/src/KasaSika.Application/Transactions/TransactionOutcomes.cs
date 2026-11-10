using KasaSika.Application.Abstractions;
using KasaSika.Application.Language;
using KasaSika.Domain.Notifications;
using KasaSika.Domain.Transactions;

namespace KasaSika.Application.Transactions;

/// <summary>
/// Side effects of a transfer reaching its final state, whoever noticed it (the user's status check, a
/// provider callback, or the background reconciler):
///  - a notification in the user's language, so they hear the result next time even if they left the app;
///  - the recipient is remembered as a frequent contact, only when money actually reached them.
/// Never saves; the caller saves in the same unit of work as the state change.
/// </summary>
public sealed class TransactionOutcomes(IKasaDbContext db, TimeProvider clock)
{
    public const string ResultKind = "TRANSFER_RESULT";

    public void Record(Transaction tx, DateTimeOffset now)
    {
        if (!TransactionStateMachine.IsFinal(tx.State) && tx.State != TransactionState.ReconciliationRequired) return;
        // Pre-submission endings (cancelled, expired, invalid recipient) were already spoken in the conversation.
        if (tx.SubmittedAt is null) return;

        var alreadyNotified = db.Notifications.Local.Any(n => n.TransactionId == tx.Id && n.Kind == ResultKind)
                              || db.Notifications.Any(n => n.TransactionId == tx.Id && n.Kind == ResultKind);
        if (!alreadyNotified)
        {
            var part = ResultReply(tx);
            db.Notifications.Add(Notification.Create(tx.UserId, tx.Id, ResultKind, tx.Language, ResponseCatalog.Render(part, tx.Language, clock), now));
        }

        if (tx.State == TransactionState.Success && tx.RecipientContactId is { } contactId)
        {
            var contact = db.Contacts.Local.FirstOrDefault(c => c.Id == contactId) ?? db.Contacts.FirstOrDefault(c => c.Id == contactId);
            contact?.RecordUse(now);
        }
    }

    /// <summary>The spoken result for a submitted transfer. Uncertainty is never described as success or failure.</summary>
    public static ReplyPart ResultReply(Transaction tx) => tx.State switch
    {
        TransactionState.Success => ReplyPart.Of("SUCCESS", new ReplyArgs { AmountMinor = tx.AmountMinor, Name = tx.RecipientName }),
        TransactionState.Failed when tx.FailureReason == "PROVIDER_HAS_NO_RECORD" => ReplyPart.Of("FAILED_NO_RECORD", new ReplyArgs { Name = tx.RecipientName }),
        TransactionState.Failed => ReplyPart.Of("FAILED", new ReplyArgs { Name = tx.RecipientName }),
        TransactionState.Unknown or TransactionState.ReconciliationRequired => ReplyPart.Of("UNKNOWN"),
        TransactionState.Cancelled => ReplyPart.Of("CANCELLED"),
        TransactionState.Expired => ReplyPart.Of("EXPIRED"),
        _ => ReplyPart.Of("PENDING"),
    };
}
