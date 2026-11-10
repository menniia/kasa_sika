using KasaSika.Domain;
using KasaSika.Domain.Transactions;

namespace KasaSika.UnitTests.Domain;

public class TransactionStateMachineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private static Transaction NewTx(DateTimeOffset? now = null) => Transaction.Create(
        Guid.NewGuid(), "key-12345678", "fp", Money.Ghs(50), "Ama Owusu", Msisdn.Parse("0240000111"), null, "en", null,
        "mock", ProviderMode.DemoSimulated, now ?? T0, TimeSpan.FromMinutes(10), "corr");

    /// <summary>Walks a transfer to AUTHORIZED the way TransactionService does.</summary>
    private static Transaction Authorized(string fingerprint = "auth-fp")
    {
        var tx = NewTx();
        tx.BeginValidation(T0, "c");
        tx.MarkReadyForConfirmation(T0, "c");
        tx.Confirm(T0, "c");
        tx.BeginAuthentication(Guid.NewGuid(), T0, "c");
        tx.MarkAuthenticated(fingerprint, "Passcode", T0, "c");
        tx.BeginAuthorization(T0, "c");
        tx.Authorize(Guid.NewGuid(), "not required", T0, "c");
        return tx;
    }

    [Fact]
    public void A_new_transfer_is_parsed_into_integer_minor_units()
    {
        var tx = NewTx();
        Assert.Equal(TransactionState.Parsed, tx.State);
        Assert.Equal(5000, tx.AmountMinor);
        Assert.Equal("GHS", tx.Currency);
        Assert.Equal(2, tx.Events.Count);
    }

    [Fact]
    public void Understanding_a_command_cannot_jump_to_money_sent()
    {
        var tx = NewTx();
        Assert.Throws<KasaException>(() => tx.BeginSubmission("fp", T0, "c"));
        Assert.False(TransactionStateMachine.CanTransition(TransactionState.Parsed, TransactionState.Success));
        Assert.False(TransactionStateMachine.CanTransition(TransactionState.ReadyForConfirmation, TransactionState.Submitting));
    }

    [Fact]
    public void Accepted_202_is_submitted_not_success()
    {
        var tx = Authorized();
        tx.BeginSubmission("auth-fp", T0, "c");
        tx.RecordProviderAccepted("mock", T0, "c");
        Assert.Equal(TransactionState.Submitted, tx.State);
        Assert.False(TransactionStateMachine.IsFinal(tx.State));
    }

    [Fact]
    public void A_timeout_is_unknown_not_failed()
    {
        var tx = Authorized();
        tx.BeginSubmission("auth-fp", T0, "c");
        tx.RecordSubmissionUncertain("timeout", "mock", T0, "c");
        Assert.Equal(TransactionState.Unknown, tx.State);
        Assert.Null(tx.FailureReason);
    }

    [Fact]
    public void Submission_is_refused_if_anything_changed_after_authentication()
    {
        var tx = Authorized("fingerprint-of-50-to-ama");
        var ex = Assert.Throws<KasaException>(() => tx.BeginSubmission("fingerprint-of-5000-to-someone-else", T0, "c"));
        Assert.Equal("AUTHORIZATION_MISMATCH", ex.Code);
    }

    [Fact]
    public void Provider_answers_are_idempotent_once_final()
    {
        var tx = Authorized();
        tx.BeginSubmission("auth-fp", T0, "c");
        tx.RecordProviderAccepted("mock", T0, "c");
        Assert.True(tx.ApplyProviderStatus(new ProviderStatusReport(ProviderTransferStatus.Successful, null, "123"), "cb", T0, "c"));
        var events = tx.Events.Count;
        // The same (or a contradicting, late) answer changes nothing: a duplicate callback cannot double-apply.
        Assert.False(tx.ApplyProviderStatus(new ProviderStatusReport(ProviderTransferStatus.Successful), "cb", T0, "c"));
        Assert.False(tx.ApplyProviderStatus(new ProviderStatusReport(ProviderTransferStatus.Failed), "cb", T0, "c"));
        Assert.Equal(TransactionState.Success, tx.State);
        Assert.Equal(events, tx.Events.Count);
        Assert.Equal("123", tx.ProviderFinancialId);
    }

    [Fact]
    public void An_unrecognised_status_is_unknown_never_success()
    {
        var tx = Authorized();
        tx.BeginSubmission("auth-fp", T0, "c");
        tx.RecordProviderAccepted("mock", T0, "c");
        tx.ApplyProviderStatus(new ProviderStatusReport(ProviderTransferStatus.Unrecognized), "r", T0, "c");
        Assert.Equal(TransactionState.Unknown, tx.State);
    }

    [Fact]
    public void Not_found_alone_does_not_fail_a_transfer()
    {
        var tx = Authorized();
        tx.BeginSubmission("auth-fp", T0, "c");
        tx.RecordSubmissionUncertain("timeout", "mock", T0, "c");
        Assert.False(tx.ApplyProviderStatus(new ProviderStatusReport(ProviderTransferStatus.NotFound), "r", T0, "c"));
        Assert.Equal(TransactionState.Unknown, tx.State);
    }

    [Fact]
    public void Cannot_cancel_after_submission()
    {
        var tx = Authorized();
        tx.BeginSubmission("auth-fp", T0, "c");
        var ex = Assert.Throws<KasaException>(() => tx.Cancel("user", T0, "c"));
        Assert.Equal("TRANSACTION_CANNOT_BE_CANCELLED", ex.Code);
    }

    [Fact]
    public void Expired_transfers_cannot_be_confirmed()
    {
        var tx = NewTx();
        tx.BeginValidation(T0, "c");
        tx.MarkReadyForConfirmation(T0, "c");
        var later = T0.AddMinutes(11);
        Assert.Equal("TRANSACTION_EXPIRED", Assert.Throws<KasaException>(() => tx.Confirm(later, "c")).Code);
        Assert.True(tx.ExpireIfDue(later, "c"));
        Assert.Equal(TransactionState.Expired, tx.State);
    }

    [Fact]
    public void Every_transition_is_recorded_with_increasing_sequence()
    {
        var tx = Authorized();
        var sequences = tx.Events.Select(e => e.Sequence).ToList();
        Assert.Equal(sequences.OrderBy(x => x), sequences);
        Assert.Equal(sequences.Count, sequences.Distinct().Count());
    }

    [Fact]
    public void Money_rejects_zero_and_negative_amounts()
    {
        Assert.Throws<KasaException>(() => Money.FromMinor(0, "GHS"));
        Assert.Throws<KasaException>(() => Money.FromMinor(-100, "GHS"));
    }
}
