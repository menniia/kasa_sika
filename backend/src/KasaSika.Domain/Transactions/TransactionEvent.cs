namespace KasaSika.Domain.Transactions;

/// <summary>
/// append-only history of a transaction. rows are only ever inserted, never updated or deleted, so this is
/// the audit trail answering "who moved this transfer to which state, when and why"
/// </summary>
public sealed class TransactionEvent
{
    private TransactionEvent() { }

    internal TransactionEvent(Guid transactionId, int sequence, TransactionState? from, TransactionState to, string actor, string reason, string correlationId, DateTimeOffset at)
    {
        Id = Guid.NewGuid();
        TransactionId = transactionId;
        Sequence = sequence;
        From = from;
        To = to;
        Actor = actor;
        Reason = reason;
        CorrelationId = correlationId;
        OccurredAt = at;
    }

    public Guid Id { get; private set; }
    public Guid TransactionId { get; private set; }
    public int Sequence { get; private set; }
    public TransactionState? From { get; private set; }
    public TransactionState To { get; private set; }
    public string Actor { get; private set; } = "";
    public string Reason { get; private set; } = "";
    public string CorrelationId { get; private set; } = "";
    public DateTimeOffset OccurredAt { get; private set; }
}