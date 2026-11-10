using KasaSika.Domain;
using KasaSika.Domain.Payments;
using KasaSika.Domain.Transactions;

namespace KasaSika.Application.Abstractions;

/// <param name="Note">Short text shown on the payee's statement. No personal data beyond what the payee already sees.</param>
public sealed record TransferRequest(Guid TransactionId, string Reference, Money Amount, Msisdn Recipient, string Note);

public enum SubmissionOutcome
{
    /// <summary>The provider accepted the request for processing (MTN: HTTP 202). Not the same as "paid".</summary>
    Accepted,
    /// <summary>The provider definitively refused it. Nothing was moved and nothing will be.</summary>
    Rejected,
    /// <summary>We cannot tell whether the provider received it (timeout, 5xx, network loss). Must be reconciled.</summary>
    Uncertain,
}

public sealed record SubmissionResult(SubmissionOutcome Outcome, string? Reason = null)
{
    public static SubmissionResult Accepted() => new(SubmissionOutcome.Accepted);
    public static SubmissionResult Rejected(string reason) => new(SubmissionOutcome.Rejected, reason);
    public static SubmissionResult Uncertain(string reason) => new(SubmissionOutcome.Uncertain, reason);
}

/// <param name="Detail">Human-readable explanation recorded on the transaction timeline.</param>
public sealed record ConsentOutcome(ConsentStatus Status, string? ProviderReference, DateTimeOffset? ExpiresAt, string Detail);

/// <summary>Balance of the account the provider credentials belong to (for Disbursements: Kasa Sika's own account).</summary>
public sealed record ProviderBalance(string AvailableBalance, string Currency);

/// <summary>
/// What this provider integration can honestly do. The conversation reads these flags rather than assuming:
/// e.g. MTN Disbursements pays out of Kasa Sika's own account, so it cannot report the USER's wallet balance.
/// </summary>
/// <param name="DebitsUserWallet">True only if a transfer takes money from the end user's own wallet.</param>
/// <param name="UserWalletBalance">True only if the provider can tell us the end user's balance.</param>
/// <param name="FundingSource">Plain-language description of where transferred money comes from.</param>
public sealed record ProviderCapabilities(bool DebitsUserWallet, bool UserWalletBalance, string FundingSource);

/// <summary>
/// What a provider callback tells us to look at. It deliberately carries NO status: callbacks are
/// unauthenticated (MTN documents no signature), so their content is never trusted. The service uses the
/// callback only as a nudge to ask the provider, over an authenticated call, what the real status is.
/// </summary>
public sealed record ProviderCallback(string ProviderReference, string Fingerprint, string Summary);

/// <summary>A transient problem talking to the provider (outage, timeout, rate limit). Safe to retry later.</summary>
public sealed class ProviderUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The port between Kasa Sika and a mobile-money provider. Implementations: MtnMomoProvider (real HTTP calls
/// to MTN) and MockPaymentProvider (no network, no money). The rest of the application cannot tell them apart.
/// </summary>
public interface IPaymentProvider
{
    string Name { get; }
    ProviderMode Mode { get; }
    ProviderCapabilities Capabilities { get; }

    /// <summary>Is this number a registered, active account at the provider? Throws <see cref="ProviderUnavailableException"/> on outage.</summary>
    Task<bool> IsAccountHolderActiveAsync(Msisdn recipient, CancellationToken cancellationToken);

    Task<ConsentOutcome> RequestConsentAsync(Guid transactionId, string scope, CancellationToken cancellationToken);

    /// <summary>
    /// Asks the provider to move the money. Never throws for a provider-side problem: it classifies the
    /// outcome so the caller can persist the right state. The caller must have already saved
    /// <see cref="TransactionState.Submitting"/> before calling this.
    /// </summary>
    Task<SubmissionResult> SubmitTransferAsync(TransferRequest request, CancellationToken cancellationToken);

    /// <summary>The provider's own answer about a reference. Throws <see cref="ProviderUnavailableException"/> on transient errors.</summary>
    Task<ProviderStatusReport> GetTransferStatusAsync(string reference, CancellationToken cancellationToken);

    /// <summary>Balance of the provider account the credentials belong to. Throws <see cref="ProviderUnavailableException"/> on outage.</summary>
    Task<ProviderBalance> GetOwnAccountBalanceAsync(CancellationToken cancellationToken);

    /// <summary>Extracts the reference from an incoming callback. Throws KasaException if the payload is unusable.</summary>
    ProviderCallback ParseCallback(IReadOnlyDictionary<string, string> headers, string body);
}

/// <summary>Demo-only controls for the mock provider. Not implemented by the MTN provider.</summary>
public interface IDemoPaymentControls
{
    IReadOnlyList<string> Scenarios { get; }
    /// <summary>Null = use each recipient's configured scenario.</summary>
    string? ActiveOverride { get; }
    void SetOverride(string? scenario);
    string ScenarioFor(Msisdn recipient);
    void Reset();
}
