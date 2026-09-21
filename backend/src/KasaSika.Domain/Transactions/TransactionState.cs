namespace KasaSika.Domain.Transactions;

/// <summary>
/// The Kasa Sika view of a transfer. Note what these states are NOT: they do not say where the money is.
/// MTN is the system of record for that. <see cref="Success"/> means "MTN told us it succeeded".
/// </summary>
public enum TransactionState
{
    /// <summary>Created from the user's request; recipient not yet validated with the provider.</summary>
    Draft,
    /// <summary>Validated. Kasa Sika has read the amount and recipient back and awaits the user's yes.</summary>
    ReadyForConfirmation,
    Confirmed,
    /// <summary>The user proved who they are *for this specific transfer* (step-up authentication).</summary>
    Authenticated,
    /// <summary>Provider-side authorization/consent is in place (or explicitly not required). Ready to submit.</summary>
    Authorized,
    /// <summary>
    /// Persisted BEFORE the provider is called. If the process dies or the network drops after this point we
    /// do not know whether MTN received the request, so this state can only move forward to a state that is
    /// resolved by asking MTN - never back, and never a second submission.
    /// </summary>
    Submitting,
    /// <summary>MTN accepted the request (HTTP 202). Accepted is NOT success; the result is asynchronous.</summary>
    Pending,
    Success,
    Failed,
    Cancelled,
    Expired,
    /// <summary>The outcome is genuinely uncertain. Never shown to the user as failed or successful; a worker resolves it.</summary>
    ReconciliationRequired,
}

public static class TransactionStateMachine
{
    private static readonly IReadOnlyDictionary<TransactionState, TransactionState[]> Allowed =
        new Dictionary<TransactionState, TransactionState[]>
        {
            [TransactionState.Draft] = [TransactionState.ReadyForConfirmation, TransactionState.Failed, TransactionState.Cancelled, TransactionState.Expired],
            [TransactionState.ReadyForConfirmation] = [TransactionState.Confirmed, TransactionState.Cancelled, TransactionState.Expired],
            [TransactionState.Confirmed] = [TransactionState.Authenticated, TransactionState.Cancelled, TransactionState.Expired],
            [TransactionState.Authenticated] = [TransactionState.Authorized, TransactionState.Failed, TransactionState.Cancelled, TransactionState.Expired],
            [TransactionState.Authorized] = [TransactionState.Submitting, TransactionState.Cancelled, TransactionState.Expired],
            [TransactionState.Submitting] = [TransactionState.Pending, TransactionState.Failed, TransactionState.ReconciliationRequired],
            [TransactionState.Pending] = [TransactionState.Success, TransactionState.Failed, TransactionState.ReconciliationRequired],
            [TransactionState.ReconciliationRequired] = [TransactionState.Pending, TransactionState.Success, TransactionState.Failed],
            [TransactionState.Success] = [],
            [TransactionState.Failed] = [],
            [TransactionState.Cancelled] = [],
            [TransactionState.Expired] = [],
        };

    public static bool CanTransition(TransactionState from, TransactionState to) => Allowed[from].Contains(to);

    public static bool IsTerminal(TransactionState state) => Allowed[state].Length == 0;

    /// <summary>States from which the user can still walk away without money having been requested.</summary>
    public static bool IsBeforeSubmission(TransactionState state) =>
        state is TransactionState.Draft or TransactionState.ReadyForConfirmation or TransactionState.Confirmed
            or TransactionState.Authenticated or TransactionState.Authorized;

    /// <summary>States where money may be in flight and only the provider can tell us the result.</summary>
    public static bool NeedsProviderResolution(TransactionState state) =>
        state is TransactionState.Submitting or TransactionState.Pending or TransactionState.ReconciliationRequired;
}
