using KasaSika.Domain.Identity;
using System.Transactions;
namespace KasaSika.Domain.Payments;

public enum ConsentStatus
{
    Pending,
    Active,
    NotRequired,
    Expired,
    Revoked,
    Failed,
}

/// <summary>The provider-side authorization context for one transaction </summary>
public sealed class Consent
{
    private Consent() { }

    public Consent(Guid userId, Guid transactionId, string provider, string scope, ConsentStatus status, string? providerReference, DateTimeOffset now, DateTimeOffset? expiredAt)
    {
        Id = Guid.NewGuid();
        UserId = userId;
        TransactionId = transactionId;
        Provider = provider;
        Scope = scope;
        Status = status;
        ProviderReference = providerReference;
        CreatedAt = now;
        ExpiredAt = expiredAt;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid TransactionId { get; private set; }
    public string Provider { get; private set; } = "";
    public string Scope { get; private set; } = "";
    public ConsentStatus Status { get; private set; }
    public string? ProviderReference { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ExpiredAt { get; private set; }

    public bool IsUsable(DateTimeOffset now) =>
        Status is ConsentStatus.Active or ConsentStatus.NotRequired && (ExpiredAt is null || ExpiredAt > now);
}

public sealed class ProviderOperation
{
    private ProviderOperation() { }

    public ProviderOperation(Guid transactionId, string provider, string operationType, string externalReference, string status, string? summary, DateTimeOffset now)
    {
        Id = Guid.NewGuid();
        TransactionId = transactionId;
        Provider = provider;
        OperationType = operationType;
        ExternalReference = externalReference;
        Status = status;
        ResponseSummary = summary;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid TransactionId { get; private set; }
    public string Provider { get; private set; } = "";
    public string OperationType { get; private set; } = "";
    /// <summary>The reference that was generated (MTN X-Reference-Id).Unique per provider: the provider-side idempotency key.</summary>
    public string ExternalReference { get; private set; } = "";
    public string Status { get; private set; } = "";
    public string? ResponseSummary { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public void Update(string status, string? summary, DateTimeOffset now)
    {
        Status = status;
        ResponseSummary = summary;
        UpdatedAt = now;
    }
}

/// <summary>A note tat a transaction's outsome was uncertain and what was done about it </summary>
public sealed class ReconciliationRecord
{
    private ReconciliationRecord() { }

    public ReconciliationRecord(Guid transactionId, string kasaState, string providerStatus, string resolution, string notes, DateTimeOffset now)
    {
        Id = Guid.NewGuid();
        TransactionId = transactionId;
        KasaState = kasaState;
        ProviderStatus = providerStatus;
        Resolution = resolution;
        Notes = notes;
        CreatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid TransactionId { get; private set; }
    public string KasaState { get; private set; } = "";
    public string ProviderStatus { get; private set; } = "";
    /// <summary>OPEN, RESOLVED_BY_PROVIDER_STATUS, RESOLVED_NO_PROVIDER_RECORD, ...</summary>
    public string Resolution { get; private set; } = "";
    public string Notes { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
}

/// <summary>proof that a given provider callback was already handled, the unique fingerprint makes duplicates harmless </summary>
public sealed class ProviderCallbackReceipt
{
    private ProviderCallbackReceipt() { }

    public ProviderCallbackReceipt(string provider, string fingerprint, string providerReference, Guid? transactionId, string summary, DateTimeOffset now)
    {
        Id = Guid.NewGuid();
        Provider = provider;
        Fingerprint = fingerprint;
        ProviderReference = providerReference;
        TransactionId = transactionId;
        Summary = summary;
        ReceivedAt = now;
    }

    public Guid Id { get; private set; }
    public string Provider { get; private set; } = "";
    public string Fingerprint { get; private set; } = "";
    public string ProviderReference { get; private set; } = "";
    public Guid? TransactionId { get; private set; }
    public string Summary { get; private set; } = "";
    public DateTimeOffset ReceivedAt { get; private set; }
}

/// <summary>security relevant events that are not a transaction state change (registration, lockout, unmatched callback, ...) </summary>
public sealed class AuditEvent
{
    private AuditEvent() { }

    public AuditEvent(Guid? userId, Guid? transactionId, string operation, string result, string? detail, string correlationId, DateTimeOffset now)
    {
        Id = Guid.NewGuid();
        UserId = userId;
        TransactionId = transactionId;
        Operation = operation;
        Result = result;
        Detail = detail;
        CorrelationId = correlationId;
        OccurredAt = now;
    }

    public Guid Id { get; private set; }
    public Guid? UserId { get; private set; }
    public Guid? TransactionId { get; private set; }
    public string Operation { get; private set; } = "";
    public string Result { get; private set; } = "";
    public string? Detail { get; private set; }
    public string CorrelationId { get; private set; } = "";
    public DateTimeOffset OccurredAt { get; private set; }
}