using KasaSika.Domain;
using KasaSika.Domain.Common;
using KasaSika.Domain.Contacts;
using KasaSika.Domain.Conversations;
using KasaSika.Domain.Identity;
using KasaSika.Domain.Notifications;
using KasaSika.Domain.Payments;
using KasaSika.Domain.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace KasaSika.Application.Abstractions;

/// <summary>
/// The application's view of the database. EF Core's DbContext already IS a unit of work and repository, so
/// services use it directly through this small interface instead of wrapping it in generic repositories.
/// The interface exists only so the Application project does not depend on the Npgsql/PostgreSQL provider.
/// </summary>
public interface IKasaDbContext
{
    DbSet<User> Users { get; }
    DbSet<Device> Devices { get; }
    DbSet<DeviceCredential> DeviceCredentials { get; }
    DbSet<Session> Sessions { get; }
    DbSet<AuthenticationAttempt> AuthenticationAttempts { get; }
    DbSet<AuthenticationChallenge> AuthenticationChallenges { get; }
    DbSet<Contact> Contacts { get; }
    DbSet<Transaction> Transactions { get; }
    DbSet<TransactionEvent> TransactionEvents { get; }
    DbSet<Consent> Consents { get; }
    DbSet<ProviderOperation> ProviderOperations { get; }
    DbSet<ReconciliationRecord> ReconciliationRecords { get; }
    DbSet<ProviderCallbackReceipt> CallbackReceipts { get; }
    DbSet<AuditEvent> AuditEvents { get; }
    DbSet<Conversation> Conversations { get; }
    DbSet<ConversationTurn> ConversationTurns { get; }
    DbSet<IdempotencyRecord> IdempotencyRecords { get; }
    DbSet<Notification> Notifications { get; }

    /// <summary>Used to discard a failed insert after losing a unique-index race (idempotent create, duplicate callback).</summary>
    ChangeTracker ChangeTracker { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    Task<bool> CanConnectAsync(CancellationToken cancellationToken = default);
}

public static class KasaDbContextExtensions
{
    /// <summary>
    /// Saves, translating "another request got there first" into a 409 the client can retry. That happens in
    /// two shapes, and both must be handled: the optimistic-concurrency check on the transaction's version
    /// (see <see cref="Transaction.Version"/>), or a unique index (e.g. the provider reference of a
    /// submission) rejecting the second writer.
    /// </summary>
    public static async Task SaveOrConflictAsync(this IKasaDbContext db, CancellationToken cancellationToken = default)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException or UniqueViolationException)
        {
            throw new KasaException(ErrorKind.Conflict, "CONCURRENT_UPDATE",
                "This transaction was changed by another request. Please check its status and try again.", retryable: true);
        }
    }
}
