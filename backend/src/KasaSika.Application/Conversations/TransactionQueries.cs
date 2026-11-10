using KasaSika.Application.Abstractions;
using KasaSika.Domain.Transactions;
using Microsoft.EntityFrameworkCore;

namespace KasaSika.Application.Conversations;

/// <summary>
/// The only questions a conversation can ask about money. Each is a fixed, owner-scoped database query:
/// natural language selects WHICH query runs, never what it does or whose data it reads.
/// </summary>
public sealed class TransactionQueries(IKasaDbContext db, TimeProvider clock)
{
    /// <summary>Most recent transfer that was actually sent to the provider.</summary>
    public Task<Transaction?> LastSubmittedAsync(Guid userId, CancellationToken ct) =>
        db.Transactions.AsNoTracking()
            .Where(t => t.UserId == userId && t.SubmittedAt != null)
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync(ct);

    /// <summary>Most recent transfer still waiting for the provider's answer, if any.</summary>
    public Task<Transaction?> LatestInFlightAsync(Guid userId, CancellationToken ct) =>
        db.Transactions.AsNoTracking()
            .Where(t => t.UserId == userId && (t.State == TransactionState.Submitted || t.State == TransactionState.Pending
                     || t.State == TransactionState.Unknown || t.State == TransactionState.Submitting))
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync(ct);

    /// <summary>Money confirmed sent today (Ghana time = GMT). Counts only transfers MTN reported successful.</summary>
    public async Task<(long AmountMinor, int Count)> SentTodayAsync(Guid userId, CancellationToken ct)
    {
        var start = new DateTimeOffset(clock.GetUtcNow().UtcDateTime.Date, TimeSpan.Zero);
        var rows = await db.Transactions.AsNoTracking()
            .Where(t => t.UserId == userId && t.State == TransactionState.Success && t.CompletedAt >= start)
            .Select(t => t.AmountMinor)
            .ToListAsync(ct);
        return (rows.Sum(), rows.Count);
    }
}
