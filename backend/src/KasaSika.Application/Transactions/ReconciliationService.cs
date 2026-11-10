using KasaSika.Application.Abstractions;
using KasaSika.Application.Options;
using KasaSika.Domain.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KasaSika.Application.Transactions;

/// <summary>
/// Finds transfers whose outcome we do not know yet and resolves them by asking the provider. Run on a
/// timer by a background worker. Split into "list ids" and "reconcile one" so the worker can give each
/// transaction its own DI scope (its own DbContext): one bad row cannot poison the rest of the batch.
/// </summary>
public sealed class ReconciliationService(
    IKasaDbContext db,
    ProviderStatusSync statusSync,
    TimeProvider clock,
    IOptions<ReconciliationOptions> options)
{
    public async Task<IReadOnlyList<Guid>> GetCandidateIdsAsync(CancellationToken ct)
    {
        var settings = options.Value;
        var staleCutoff = clock.GetUtcNow().AddMinutes(-settings.StaleSubmittingMinutes);
        return await db.Transactions.AsNoTracking()
            .Where(t => t.State == TransactionState.Submitted
                     || t.State == TransactionState.Pending
                     || t.State == TransactionState.Unknown
                     || t.State == TransactionState.ReconciliationRequired
                     || (t.State == TransactionState.Submitting && t.UpdatedAt <= staleCutoff))
            .OrderBy(t => t.UpdatedAt)
            .Select(t => t.Id)
            .Take(settings.BatchSize)
            .ToListAsync(ct);
    }

    /// <returns>True if the transaction's state changed.</returns>
    public async Task<bool> ReconcileAsync(Guid transactionId, CancellationToken ct)
    {
        var tx = await db.Transactions.Include(t => t.Events).FirstOrDefaultAsync(t => t.Id == transactionId, ct);
        if (tx is null) return false;
        var changed = await statusSync.SyncAsync(tx, "system:reconciler", $"reconciler-{Guid.NewGuid():N}", ct);
        if (changed) await db.SaveOrConflictAsync(ct);
        return changed;
    }

    /// <summary>Convenience for tests and one-off runs: reconciles every candidate using this instance's DbContext.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var changed = 0;
        foreach (var id in await GetCandidateIdsAsync(ct))
            if (await ReconcileAsync(id, ct)) changed++;
        return changed;
    }
}
