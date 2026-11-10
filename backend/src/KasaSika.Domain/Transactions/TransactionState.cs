namespace KasaSika.Domain.Transactions;

/// <summary>
/// The Kasa Sika view of a transfer. Note what these states are NOT: they do not say where the money is.
/// MTN is the system of record for that. <see cref="Success"/> means "MTN told us it succeeded".
/// </summary>
public enum TransactionState
{
    /// <summary>Created from the user's request.</summary>
    Draft,
    /// <summary>The request was turned into a strict structured transfer (amount in minor units, currency, recipient).</summary>
    Parsed,
    /// <summary>Persisted BEFORE asking the provider whether the recipient's account is active.</summary>
    Validating,
    /// <summary>Validated. Kasa Sika has read the amount and recipient back and awaits the user's yes.</summary>
    ReadyForConfirmation,
    Confirmed,
    /// <summary>A transaction-bound authentication challenge was issued; waiting for the user's proof.</summary>
    AuthenticationPending,
    /// <summary>The user proved who they are *for this specific transfer* (step-up authentication).</summary>
    Authenticated,
    /// <summary>Provider-side authorization/consent has been requested.</summary>
    AuthorizationPending,
    /// <summary>Provider-side authorization/consent is in place (or explicitly not required). Ready to submit.</summary>
    Authorized,
    /// <summary>
    /// Persisted BEFORE the provider is called. If the process dies or the network drops after this point we
    /// do not know whether MTN received the request, so this state can only move forward to a state that is
    /// resolved by asking MTN - never back, and never a second submission.
    /// </summary>
    Submitting,
    /// <summary>MTN accepted the request (HTTP 202). Accepted is NOT success; the result is asynchronous.</summary>
    Submitted,
    /// <summary>MTN confirmed it is still processing the transfer.</summary>
    Pending,
    Success,
    Failed,
    Cancelled,
    Expired,
    /// <summary>
    /// The outcome is genuinely uncertain (timeout, dropped connection, unrecognised provider status). Never
    /// shown to the user as failed or successful; the reconciler keeps asking MTN.
    /// </summary>
    Unknown,
    /// <summary>A successful transfer that was later refunded/reversed at the provider.</summary>
    Reversed,
    /// <summary>Still uncertain after automatic checks: needs an operator to reconcile with MTN.</summary>
    ReconciliationRequired,
}

public static class TransactionStateMachine
{
    private static readonly IReadOnlyDictionary<TransactionState, TransactionState[]> Allowed =
        new Dictionary<TransactionState, TransactionState[]>
        {
            [TransactionState.Draft] = [TransactionState.Parsed, TransactionState.Failed, TransactionState.Cancelled, TransactionState.Expired],
            [TransactionState.Parsed] = [TransactionState.Validating, TransactionState.Failed, TransactionState.Cancelled, TransactionState.Expired],
            // Back to Parsed only when the provider could not be reached: the validation simply did not happen.
            [TransactionState.Validating] = [TransactionState.ReadyForConfirmation, TransactionState.Parsed, TransactionState.Failed, TransactionState.Cancelled, TransactionState.Expired],
            [TransactionState.ReadyForConfirmation] = [TransactionState.Confirmed, TransactionState.Cancelled, TransactionState.Expired],
            [TransactionState.Confirmed] = [TransactionState.AuthenticationPending, TransactionState.Cancelled, TransactionState.Expired],
            [TransactionState.AuthenticationPending] = [TransactionState.Authenticated, TransactionState.Cancelled, TransactionState.Expired],
            [TransactionState.Authenticated] = [TransactionState.AuthorizationPending, TransactionState.Cancelled, TransactionState.Expired],
            [TransactionState.AuthorizationPending] = [TransactionState.Authorized, TransactionState.Failed, TransactionState.Cancelled, TransactionState.Expired],
            [TransactionState.Authorized] = [TransactionState.Submitting, TransactionState.Cancelled, TransactionState.Expired],
            [TransactionState.Submitting] = [TransactionState.Submitted, TransactionState.Failed, TransactionState.Unknown],
            [TransactionState.Submitted] = [TransactionState.Pending, TransactionState.Success, TransactionState.Failed, TransactionState.Unknown],
            [TransactionState.Pending] = [TransactionState.Success, TransactionState.Failed, TransactionState.Unknown],
            [TransactionState.Unknown] = [TransactionState.Pending, TransactionState.Success, TransactionState.Failed, TransactionState.ReconciliationRequired],
            [TransactionState.ReconciliationRequired] = [TransactionState.Pending, TransactionState.Success, TransactionState.Failed],
            [TransactionState.Success] = [TransactionState.Reversed],
            [TransactionState.Failed] = [],
            [TransactionState.Cancelled] = [],
            [TransactionState.Expired] = [],
            [TransactionState.Reversed] = [],
        };

    public static bool CanTransition(TransactionState from, TransactionState to) => Allowed[from].Contains(to);

    /// <summary>No further automatic change will happen. (Success can still be reversed by a later refund.)</summary>
    public static bool IsFinal(TransactionState state) =>
        state is TransactionState.Success or TransactionState.Failed or TransactionState.Cancelled
            or TransactionState.Expired or TransactionState.Reversed;

    /// <summary>States from which the user can still walk away without money having been requested.</summary>
    public static bool IsBeforeSubmission(TransactionState state) =>
        state is TransactionState.Draft or TransactionState.Parsed or TransactionState.Validating
            or TransactionState.ReadyForConfirmation or TransactionState.Confirmed
            or TransactionState.AuthenticationPending or TransactionState.Authenticated
            or TransactionState.AuthorizationPending or TransactionState.Authorized;

    /// <summary>States where money may be in flight and only the provider can tell us the result.</summary>
    public static bool NeedsProviderResolution(TransactionState state) =>
        state is TransactionState.Submitting or TransactionState.Submitted or TransactionState.Pending
            or TransactionState.Unknown or TransactionState.ReconciliationRequired;

    /// <summary>Counts toward the daily limit: sent, or possibly sent.</summary>
    public static bool MayHaveMovedMoney(TransactionState state) =>
        NeedsProviderResolution(state) || state == TransactionState.Success;
}
