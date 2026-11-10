namespace KasaSika.Domain.Transactions;

public enum ProviderMode
{
    /// <summary>no provider contacted and no money moves</summary>
    DemoSimulated,
    /// <summary>the provider's test env. real API, fake money</summary>
    Sandbox,
    /// <summary>real money</summary>
    Live,
}

/// <summary>what the provider says about a transfer we submitted earlier</summary>
public enum ProviderTransferStatus
{
    Pending,
    Successful,
    Failed,
    /// <summary>provider has no record of that reference</summary>
    NotFound,
    /// <summary>provider returned a status text not recognised, treated as uncertain, never success</summary>
    Unrecognized,
}

/// <param name="ReasonCode">Provider's machine reason for a failure, e.g. NOT_ENOUGH_FUNDS. No personal data.</param>
/// <param name="FinancialTransactionId">The provider's own transaction id, shown to the user as the reference.</param>
public sealed record ProviderStatusReport(ProviderTransferStatus Status, string? ReasonCode = null, string? FinancialTransactionId = null);

public sealed class Transaction
{
    private readonly List<TransactionEvent> _events = [];

    private Transaction() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }

    /// <summary>client-supplied key making "create" safe to retry, unique per user </summary>
    public string IdempotencyKey { get; private set; } = "";
    /// <summary>keyed hash of the request, to detect a key being reused for a *different* request</summary>
    public string RequestFingerprint { get; private set; } = "";

    public long AmountMinor { get; private set; }
    public string Currency { get; private set; } = "";
    public Money Amount => Money.FromMinor(AmountMinor, Currency);

    public string RecipientName { get; private set; } = "";
    /// <summary>international digits, encrypted at rest, never returned by the API</summary>
    public string RecipientMsisdn { get; private set; } = "";
    public string RecipientMasked { get; private set; } = "";
    public Guid? RecipientContactId { get; private set; }

    /// <summary>Language the user was speaking when they asked ("en", "tw"). Used for status announcements.</summary>
    public string Language { get; private set; } = "en";
    public Guid? ConversationId { get; private set; }

    public TransactionState State { get; private set; }
    public string Provider { get; private set; } = "";
    public ProviderMode Mode { get; private set; }
    /// <summary>
    /// The reference we send to the provider (MTN X-Reference-Id, which is also its idempotency key). It is fixed
    /// when submission BEGINS - before the provider is called - so if we crash mid-call we can still ask the
    /// provider "what happened to reference X?". It being set does not mean the provider accepted anything.
    /// </summary>
    public string? ProviderReference { get; private set; }
    /// <summary>The provider's own id for the completed transfer (MTN financialTransactionId), once known.</summary>
    public string? ProviderFinancialId { get; private set; }

    /// <summary>binds the step-up authentication to this exact user/amount/recipient. rechecked at submission </summary>
    public string? AuthorizationFingerprint { get; private set; }
    public string? AuthenticationMethod { get; private set; }
    public Guid? ConsentId { get; private set; }

    /// <summary>Short machine code, e.g. "PROVIDER_REJECTED:INVALID_CURRENCY". Contains no personal data.</summary>
    public string? FailureReason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? SubmittedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    /// <summary>The user must complete the flow before this; it only guards the steps *before* submission.</summary>
    public DateTimeOffset ExpiresAt { get; private set; }

    public int Version { get; private set; }

    public IReadOnlyList<TransactionEvent> Events => _events;

    public static Transaction Create(
        Guid userId, string idempotencyKey, string requestFingerprint, Money amount,
        string recipientName, Msisdn recipient, Guid? recipientContactId, string language, Guid? conversationId,
        string provider, ProviderMode mode, DateTimeOffset now, TimeSpan timeToLive, string correlationId)
    {
        var tx = new Transaction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            IdempotencyKey = idempotencyKey,
            RequestFingerprint = requestFingerprint,
            AmountMinor = amount.AmountMinor,
            Currency = amount.Currency,
            RecipientName = recipientName,
            RecipientMsisdn = recipient.International,
            RecipientMasked = recipient.Masked,
            RecipientContactId = recipientContactId,
            Language = language,
            ConversationId = conversationId,
            State = TransactionState.Draft,
            Provider = provider,
            Mode = mode,
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = now + timeToLive,
        };
        tx._events.Add(new TransactionEvent(tx.Id, 0, null, TransactionState.Draft, "user", "Transaction created from user request", correlationId, now));
        // The amount and recipient arrive here already typed and validated (Money, Msisdn): that IS the parse.
        tx.Move(TransactionState.Parsed, "system:intent", $"Structured transfer: {amount} to {recipientName} ({recipient.Masked})", now, correlationId);
        return tx;
    }

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    // --- steps before money is requested ---------------------------------------------------------------

    /// <summary>Saved before asking the provider about the recipient.</summary>
    public void BeginValidation(DateTimeOffset now, string correlationId) =>
        Move(TransactionState.Validating, "system:validator", "Checking the recipient's account with the provider", now, correlationId);

    /// <summary>The provider could not be reached: validation did not happen, so it can be retried.</summary>
    public void ValidationInterrupted(DateTimeOffset now, string correlationId) =>
        Move(TransactionState.Parsed, "system:validator", "Provider unavailable; recipient not yet validated", now, correlationId);

    public void MarkReadyForConfirmation(DateTimeOffset now, string correlationId) =>
        Move(TransactionState.ReadyForConfirmation, "system:validator", "Recipient validated; awaiting user confirmation", now, correlationId);

    public void FailValidation(string reason, DateTimeOffset now, string correlationId)
    {
        FailureReason = reason;
        Move(TransactionState.Failed, "system:validator", reason, now, correlationId);
    }

    public void Confirm(DateTimeOffset now, string correlationId)
    {
        EnsureNotExpired(now);
        Move(TransactionState.Confirmed, "user", "User explicitly confirmed amount and recipient", now, correlationId);
    }

    public void BeginAuthentication(Guid challengeId, DateTimeOffset now, string correlationId)
    {
        EnsureNotExpired(now);
        if (State == TransactionState.AuthenticationPending) return; // a fresh challenge for the same step
        Move(TransactionState.AuthenticationPending, "system:authentication", $"Authentication challenge {challengeId:N} issued", now, correlationId);
    }

    public void MarkAuthenticated(string authorizationFingerprint, string method, DateTimeOffset now, string correlationId)
    {
        EnsureNotExpired(now);
        AuthorizationFingerprint = authorizationFingerprint;
        AuthenticationMethod = method;
        Move(TransactionState.Authenticated, "system:authentication", $"Step-up authentication succeeded ({method})", now, correlationId);
    }

    public void BeginAuthorization(DateTimeOffset now, string correlationId)
    {
        EnsureNotExpired(now);
        Move(TransactionState.AuthorizationPending, "system:authorization", "Requesting provider authorization", now, correlationId);
    }

    public void Authorize(Guid consentId, string detail, DateTimeOffset now, string correlationId)
    {
        EnsureNotExpired(now);
        ConsentId = consentId;
        Move(TransactionState.Authorized, "system:authorization", detail, now, correlationId);
    }

    public void FailAuthorization(string reason, DateTimeOffset now, string correlationId)
    {
        FailureReason = reason;
        Move(TransactionState.Failed, "system:authorization", reason, now, correlationId);
    }

    public void Cancel(string actor, DateTimeOffset now, string correlationId)
    {
        if (!TransactionStateMachine.IsBeforeSubmission(State))
            throw KasaException.Conflict("TRANSACTION_CANNOT_BE_CANCELLED", "This transaction has already been sent to the provider and can no longer be cancelled.");
        Move(TransactionState.Cancelled, actor, "Cancelled before anything was sent to the provider", now, correlationId);
    }

    /// <summary>Moves a stale, not-yet-submitted transaction to Expired. Returns false if nothing changed.</summary>
    public bool ExpireIfDue(DateTimeOffset now, string correlationId)
    {
        if (!IsExpired(now) || !TransactionStateMachine.IsBeforeSubmission(State)) return false;
        Move(TransactionState.Expired, "system:expiry", "Not completed before it expired", now, correlationId);
        return true;
    }

    // ---- Submission ----------------------------------------------------------------------------------------

    /// <summary>
    /// Step 1 of submitting. The caller MUST save this to the database before contacting the provider (see
    /// TransactionService.SubmitAsync). Re-computing the fingerprint here and comparing it to the one taken
    /// at authentication guarantees that what we send is exactly what the user authenticated.
    /// </summary>
    public void BeginSubmission(string currentFingerprint, DateTimeOffset now, string correlationId)
    {
        // Order matters for the error the caller sees: a wrongly-ordered call is a state error, not a tampering error.
        if (!TransactionStateMachine.CanTransition(State, TransactionState.Submitting))
            throw KasaException.Conflict("INVALID_TRANSACTION_TRANSITION", $"A transaction in state {State} cannot be submitted.");
        EnsureNotExpired(now);
        if (AuthorizationFingerprint is null || !string.Equals(AuthorizationFingerprint, currentFingerprint, StringComparison.Ordinal))
            throw KasaException.Conflict("AUTHORIZATION_MISMATCH", "The transaction changed after it was authenticated. Please start again.");
        ProviderReference = Id.ToString("D");
        SubmittedAt = now;
        Move(TransactionState.Submitting, "system:orchestrator", "Submitting to payment provider", now, correlationId);
    }

    public void RecordProviderAccepted(string providerName, DateTimeOffset now, string correlationId) =>
        Move(TransactionState.Submitted, $"provider:{providerName}", "Provider accepted the request (not yet a result)", now, correlationId);

    /// <summary>The provider definitively refused (nothing was, or will be, moved).</summary>
    public void RecordSubmissionRejected(string reason, string providerName, DateTimeOffset now, string correlationId)
    {
        FailureReason = reason;
        Move(TransactionState.Failed, $"provider:{providerName}", $"Provider rejected the request: {reason}", now, correlationId);
    }

    /// <summary>We cannot tell whether the provider received the request (timeout, 5xx, dropped connection).</summary>
    public void RecordSubmissionUncertain(string reason, string providerName, DateTimeOffset now, string correlationId) =>
        Move(TransactionState.Unknown, $"provider:{providerName}", $"Outcome uncertain: {reason}", now, correlationId);

    // ---- Resolution ----------------------------------------------------------------------------------------

    /// <summary>
    /// Applies what the provider says NOW about a transfer we submitted. Idempotent: a repeated or late
    /// answer for an already-final transaction changes nothing. Returns true if the state changed.
    /// </summary>
    public bool ApplyProviderStatus(ProviderStatusReport report, string actor, DateTimeOffset now, string correlationId)
    {
        if (TransactionStateMachine.IsFinal(State)) return false;
        if (State is not (TransactionState.Submitted or TransactionState.Pending or TransactionState.Unknown or TransactionState.ReconciliationRequired))
            throw KasaException.Conflict("INVALID_TRANSACTION_STATE", "This transaction has not been accepted by the provider yet.");

        if (report.FinancialTransactionId is { Length: > 0 } fid) ProviderFinancialId = fid;

        switch (report.Status)
        {
            case ProviderTransferStatus.Successful:
                CompletedAt = now;
                Move(TransactionState.Success, actor, "Provider confirmed success", now, correlationId);
                return true;
            case ProviderTransferStatus.Failed:
                FailureReason ??= report.ReasonCode is { Length: > 0 } code ? $"PROVIDER_FAILED:{code}" : "PROVIDER_REPORTED_FAILURE";
                CompletedAt = now;
                Move(TransactionState.Failed, actor, $"Provider reported failure{(report.ReasonCode is null ? "" : $" ({report.ReasonCode})")}", now, correlationId);
                return true;
            case ProviderTransferStatus.Pending:
                if (State == TransactionState.Pending) return false;
                Move(TransactionState.Pending, actor, "Provider confirmed the transfer is still processing", now, correlationId);
                return true;
            case ProviderTransferStatus.NotFound:
                // Not enough on its own to declare failure; the reconciler decides after a grace period.
                return false;
            default:
                if (State is TransactionState.Unknown or TransactionState.ReconciliationRequired) return false;
                Move(TransactionState.Unknown, actor, "Provider returned an unrecognised status", now, correlationId);
                return true;
        }
    }

    /// <summary>Automatic checks could not resolve an uncertain transfer; an operator must reconcile it with MTN.</summary>
    public void EscalateToReconciliation(DateTimeOffset now, string correlationId) =>
        Move(TransactionState.ReconciliationRequired, "system:reconciler", "Still uncertain after automatic checks; operator reconciliation required", now, correlationId);

    /// <summary>The provider has no record of a submission we were unsure about, and enough time has passed.</summary>
    public void ResolveAsNeverReceived(DateTimeOffset now, string correlationId)
    {
        FailureReason = "PROVIDER_HAS_NO_RECORD";
        CompletedAt = now;
        Move(TransactionState.Failed, "system:reconciler", "Provider has no record of this submission after the grace period", now, correlationId);
    }

    public void MarkReversed(string reason, DateTimeOffset now, string correlationId) =>
        Move(TransactionState.Reversed, "system:refund", reason, now, correlationId);

    // ---- Internals -----------------------------------------------------------------------------------------

    // Only throws. Recording the Expired state is the service's job (it must be saved *before* the error
    // propagates, otherwise the rollback of the failed request would also roll back the expiry).
    private void EnsureNotExpired(DateTimeOffset now)
    {
        if (IsExpired(now))
            throw new KasaException(ErrorKind.Gone, "TRANSACTION_EXPIRED", "This transaction expired. Please start again.");
    }

    private void Move(TransactionState next, string actor, string reason, DateTimeOffset now, string correlationId)
    {
        if (!TransactionStateMachine.CanTransition(State, next))
            throw KasaException.Conflict("INVALID_TRANSACTION_TRANSITION", $"Cannot move a transaction from {State} to {next}.");
        var previous = State;
        State = next;
        UpdatedAt = now;
        Version++;
        _events.Add(new TransactionEvent(Id, Version, previous, next, actor, reason, correlationId, now)); // Version was just incremented: 1, 2, 3, ...
    }
}
