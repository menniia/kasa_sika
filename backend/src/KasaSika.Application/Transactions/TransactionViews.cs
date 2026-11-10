using KasaSika.Application.Language;
using KasaSika.Domain.Transactions;

namespace KasaSika.Application.Transactions;

public sealed record RecipientView(string DisplayName, string MaskedIdentifier);

public sealed record TransactionEventView(TransactionState? From, TransactionState To, string Actor, string Reason, DateTimeOffset OccurredAt);

/// <summary>
/// "What was requested / what was authorized / what the provider reports / where it stands now" - the four
/// things a user needs to trust a financial action, in plain language.
/// </summary>
public sealed record TrustView(string Requested, string? Authorized, string ProviderReport, string Current);

/// <summary>
/// What the API returns for a transaction. Note what is absent: the recipient's full phone number, the
/// authorization fingerprint, and anything internal to the provider. Enums serialise as SCREAMING_SNAKE
/// strings (READY_FOR_CONFIRMATION, SANDBOX, ...).
/// </summary>
/// <param name="StatusLabel">Short human status in the requested language: "Successful", "Processing", "Status unknown"...</param>
/// <param name="StatusTone">success | pending | failed | unknown | neutral. For icons and shapes, never colour alone.</param>
/// <param name="Reference">What to quote to MTN or support: the provider's transaction id if known, else our reference.</param>
public sealed record TransactionView(
    Guid Id,
    TransactionState State,
    string StatusLabel,
    string StatusTone,
    bool IsFinal,
    ProviderMode Mode,
    string Notice,
    long AmountMinor,
    string Currency,
    string AmountText,
    string SpokenSummary,
    string SpokenStatus,
    RecipientView Recipient,
    string? FailureReason,
    string? Reference,
    string? AuthenticationMethod,
    TrustView Trust,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<TransactionEventView> Events);

public static class TransactionMapper
{
    public static TransactionView ToView(Transaction tx, string language, TimeProvider clock) => new(
        tx.Id,
        tx.State,
        Label(tx.State),
        Tone(tx.State),
        TransactionStateMachine.IsFinal(tx.State),
        tx.Mode,
        NoticeFor(tx.Mode),
        tx.AmountMinor,
        tx.Currency,
        $"GH₵ {tx.AmountMinor / 100:N0}.{tx.AmountMinor % 100:00}",
        ResponseCatalog.Render(ReplyPart.Of("CONFIRM_SEND", new ReplyArgs { AmountMinor = tx.AmountMinor, Name = tx.RecipientName, LastFour = tx.RecipientMasked[^4..] }), language, clock),
        SpokenStatus(tx, language, clock),
        new RecipientView(tx.RecipientName, tx.RecipientMasked),
        tx.FailureReason,
        tx.ProviderFinancialId ?? tx.ProviderReference?[..8].ToUpperInvariant(),
        tx.AuthenticationMethod,
        Trust(tx),
        tx.CreatedAt,
        tx.UpdatedAt,
        tx.ExpiresAt,
        tx.Events.OrderBy(e => e.Sequence).Select(e => new TransactionEventView(e.From, e.To, e.Actor, e.Reason, e.OccurredAt)).ToList());

    private static string NoticeFor(ProviderMode mode) => mode switch
    {
        ProviderMode.Live => "This transfer uses real MTN funds.",
        ProviderMode.Sandbox => "MTN sandbox (test environment). No real money moved.",
        _ => "Demo with mock payments. No real money moved.",
    };

    /// <summary>
    /// Wording matters here: "accepted" is never spoken as "sent", and an uncertain result is never spoken as
    /// either success or failure.
    /// </summary>
    public static string SpokenStatus(Transaction tx, string language, TimeProvider clock) => tx.State switch
    {
        TransactionState.Submitting or TransactionState.Submitted or TransactionState.Pending or
        TransactionState.Success or TransactionState.Failed or TransactionState.Unknown or
        TransactionState.ReconciliationRequired or TransactionState.Cancelled or TransactionState.Expired
            => ResponseCatalog.Render(TransactionOutcomes.ResultReply(tx), language, clock),
        _ => "",
    };

    public static string Label(TransactionState state) => state switch
    {
        TransactionState.Draft or TransactionState.Parsed or TransactionState.Validating => "Checking recipient",
        TransactionState.ReadyForConfirmation => "Waiting for your confirmation",
        TransactionState.Confirmed or TransactionState.AuthenticationPending => "Waiting for verification",
        TransactionState.Authenticated or TransactionState.AuthorizationPending or TransactionState.Authorized => "Verified",
        TransactionState.Submitting or TransactionState.Submitted or TransactionState.Pending => "Processing",
        TransactionState.Success => "Successful",
        TransactionState.Failed => "Failed",
        TransactionState.Cancelled => "Cancelled",
        TransactionState.Expired => "Expired",
        TransactionState.Unknown or TransactionState.ReconciliationRequired => "Status unknown",
        TransactionState.Reversed => "Reversed",
        _ => state.ToString(),
    };

    public static string Tone(TransactionState state) => state switch
    {
        TransactionState.Success => "success",
        TransactionState.Failed or TransactionState.Reversed => "failed",
        TransactionState.Unknown or TransactionState.ReconciliationRequired => "unknown",
        TransactionState.Submitting or TransactionState.Submitted or TransactionState.Pending => "pending",
        _ => "neutral",
    };

    private static TrustView Trust(Transaction tx)
    {
        var requested = $"GH₵ {tx.AmountMinor / 100:N0}.{tx.AmountMinor % 100:00} to {tx.RecipientName} ({tx.RecipientMasked})";
        var authEvent = tx.Events.FirstOrDefault(e => e.To == TransactionState.Authenticated);
        var authorized = authEvent is null ? null
            : $"You verified with {Method(tx.AuthenticationMethod)} at {authEvent.OccurredAt:HH:mm} GMT, for exactly this amount and recipient";
        var report = tx.State switch
        {
            TransactionState.Success => $"{Provider(tx)} reports: successful{(tx.ProviderFinancialId is null ? "" : $" (ref {tx.ProviderFinancialId})")}",
            TransactionState.Failed when tx.SubmittedAt is not null => $"{Provider(tx)} reports: failed{(tx.FailureReason is null ? "" : $" ({tx.FailureReason})")}",
            TransactionState.Submitted or TransactionState.Pending => $"{Provider(tx)} accepted the request and is processing it",
            TransactionState.Submitting => $"Sending to {Provider(tx)}",
            TransactionState.Unknown or TransactionState.ReconciliationRequired => $"No confirmed answer from {Provider(tx)} yet - not marked successful or failed",
            _ => $"Nothing has been sent to {Provider(tx)}",
        };
        return new TrustView(requested, authorized, report, Label(tx.State));
    }

    private static string Provider(Transaction tx) => tx.Mode switch
    {
        ProviderMode.DemoSimulated => "The mock payment provider",
        ProviderMode.Sandbox => "MTN sandbox",
        _ => "MTN",
    };

    private static string Method(string? method) => method switch
    {
        "Passcode" => "your Kasa Sika passcode",
        "DeviceBiometric" => "your fingerprint on this phone",
        "DemoSimulated" => "demo verification (prototype only)",
        _ => "step-up verification",
    };
}
