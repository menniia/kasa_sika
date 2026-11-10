using KasaSika.Application.Abstractions;
using KasaSika.Application.Options;
using KasaSika.Domain.Payments;
using KasaSika.Domain.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KasaSika.Application.Transactions;

/// <summary>
/// Asks the provider what really happened to a transaction and applies the answer. Shared by the three
/// things that can trigger it: the user asking for status, a provider callback, and the background
/// reconciler - so all three behave identically. Never saves; the caller saves (and handles conflicts).
/// </summary>
public sealed class ProviderStatusSync(
    IKasaDbContext db,
    IPaymentProvider provider,
    TransactionOutcomes outcomes,
    TimeProvider clock,
    IOptions<ReconciliationOptions> options,
    ILogger<ProviderStatusSync> logger)
{
    /// <returns>True if the transaction changed and needs saving.</returns>
    public async Task<bool> SyncAsync(Transaction tx, string actor, string correlationId, CancellationToken ct)
    {
        var settings = options.Value;
        var now = clock.GetUtcNow();
        var changed = false;

        if (tx.State == TransactionState.Submitting)
        {
            // We saved SUBMITTING and then never recorded MTN's answer: the process died or the DB failed
            // mid-call. We do not know whether MTN received the request, so it becomes UNKNOWN and is resolved
            // by asking MTN about the same reference - never by submitting again.
            if (now - tx.UpdatedAt < TimeSpan.FromMinutes(settings.StaleSubmittingMinutes)) return false;
            tx.RecordSubmissionUncertain("submission was interrupted before the provider's answer was recorded", provider.Name, now, correlationId);
            AddRecord(tx, "UNKNOWN", "OPEN", "Stopped between saving SUBMITTING and recording the provider's answer.", now);
            changed = true;
        }

        if (tx.State is not (TransactionState.Submitted or TransactionState.Pending or TransactionState.Unknown or TransactionState.ReconciliationRequired)
            || tx.ProviderReference is null)
            return changed;

        ProviderStatusReport report;
        try
        {
            report = await provider.GetTransferStatusAsync(tx.ProviderReference, ct);
        }
        catch (ProviderUnavailableException ex)
        {
            // A failed CHECK says nothing about the money. Leave the state alone and try again later.
            logger.LogWarning(ex, "Status check for transaction {TransactionId} failed transiently; state unchanged", tx.Id);
            return changed | EscalateIfStale(tx, now, correlationId);
        }

        if (report.Status == ProviderTransferStatus.NotFound)
        {
            if (tx.State is TransactionState.Unknown or TransactionState.ReconciliationRequired)
            {
                // The provider has no record. That is only safe to call "never received" after a grace period,
                // because a request that was in flight when we lost track may still arrive.
                if (now - (tx.SubmittedAt ?? tx.UpdatedAt) < TimeSpan.FromMinutes(settings.NotFoundGraceMinutes)) return changed;
                tx.ResolveAsNeverReceived(now, correlationId);
                AddRecord(tx, "NOT_FOUND", "RESOLVED_NO_PROVIDER_RECORD", "Provider has no record of the reference after the grace period.", now);
                await UpdateOperationAsync(tx, "NOT_RECEIVED", now, ct);
                outcomes.Record(tx, now);
                return true;
            }
            // We think MTN accepted it but MTN does not know it: an anomaly, not an answer.
            report = report with { Status = ProviderTransferStatus.Unrecognized };
        }

        var previous = tx.State;
        if (!tx.ApplyProviderStatus(report, actor, now, correlationId))
            return changed | EscalateIfStale(tx, now, correlationId);

        if (tx.State == TransactionState.Unknown)
            AddRecord(tx, report.Status.ToString(), "OPEN", "Provider returned a status that could not be interpreted.", now);
        else if (previous is TransactionState.Unknown or TransactionState.ReconciliationRequired)
            AddRecord(tx, report.Status.ToString(), "RESOLVED_BY_PROVIDER_STATUS", $"Provider reported {report.Status}.", now);

        await UpdateOperationAsync(tx, tx.State.ToString().ToUpperInvariant(), now, ct);
        outcomes.Record(tx, now);
        return true;
    }

    /// <summary>An UNKNOWN transfer that automatic checks could not resolve goes to a human.</summary>
    private bool EscalateIfStale(Transaction tx, DateTimeOffset now, string correlationId)
    {
        if (tx.State != TransactionState.Unknown) return false;
        if (now - (tx.SubmittedAt ?? tx.UpdatedAt) < TimeSpan.FromMinutes(options.Value.EscalateUnknownAfterMinutes)) return false;
        tx.EscalateToReconciliation(now, correlationId);
        AddRecord(tx, "UNKNOWN", "ESCALATED_TO_OPERATOR", "Still uncertain after automatic checks.", now);
        outcomes.Record(tx, now);
        return true;
    }

    private void AddRecord(Transaction tx, string providerStatus, string resolution, string notes, DateTimeOffset now) =>
        db.ReconciliationRecords.Add(new ReconciliationRecord(tx.Id, tx.State.ToString(), providerStatus, resolution, notes, now));

    private async Task UpdateOperationAsync(Transaction tx, string status, DateTimeOffset now, CancellationToken ct)
    {
        var op = await db.ProviderOperations.FirstOrDefaultAsync(o => o.TransactionId == tx.Id && o.OperationType == "TRANSFER", ct);
        op?.Update(status, null, now);
    }
}
