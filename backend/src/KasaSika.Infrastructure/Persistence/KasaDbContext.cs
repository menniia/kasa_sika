using KasaSika.Application.Abstractions;
using KasaSika.Domain.Common;
using KasaSika.Domain.Contacts;
using KasaSika.Domain.Conversations;
using KasaSika.Domain.Identity;
using KasaSika.Domain.Notifications;
using KasaSika.Domain.Payments;
using KasaSika.Domain.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql;

namespace KasaSika.Infrastructure.Persistence;

/// <summary>
/// PostgreSQL is authoritative for Kasa Sika's APPLICATION state (users, sessions, conversations, the
/// transfer state machine, audit). It is NOT a mirror of anyone's wallet: there is no balance column
/// anywhere, and MTN remains the financial system of record. If the two disagree, reconciliation decides
/// by asking MTN.
/// </summary>
public sealed class KasaDbContext(DbContextOptions<KasaDbContext> options, IFieldProtector protector) : DbContext(options), IKasaDbContext
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DeviceCredential> DeviceCredentials => Set<DeviceCredential>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<AuthenticationAttempt> AuthenticationAttempts => Set<AuthenticationAttempt>();
    public DbSet<AuthenticationChallenge> AuthenticationChallenges => Set<AuthenticationChallenge>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<TransactionEvent> TransactionEvents => Set<TransactionEvent>();
    public DbSet<Consent> Consents => Set<Consent>();
    public DbSet<ProviderOperation> ProviderOperations => Set<ProviderOperation>();
    public DbSet<ReconciliationRecord> ReconciliationRecords => Set<ReconciliationRecord>();
    public DbSet<ProviderCallbackReceipt> CallbackReceipts => Set<ProviderCallbackReceipt>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationTurn> ConversationTurns => Set<ConversationTurn>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<Notification> Notifications => Set<Notification>();

    public Task<bool> CanConnectAsync(CancellationToken cancellationToken = default) => Database.CanConnectAsync(cancellationToken);

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            throw new UniqueViolationException(pg.ConstraintName, ex);
        }
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // Enums as readable text: `select state from transactions` should say SUCCESS-like words, not 12.
        builder.Properties<TransactionState>().HaveConversion<string>().HaveMaxLength(40);
        builder.Properties<ProviderMode>().HaveConversion<string>().HaveMaxLength(20);
        builder.Properties<ConsentStatus>().HaveConversion<string>().HaveMaxLength(20);
        builder.Properties<AuthenticationMethod>().HaveConversion<string>().HaveMaxLength(30);
        builder.Properties<AuthenticationResult>().HaveConversion<string>().HaveMaxLength(20);
        builder.Properties<ConversationStage>().HaveConversion<string>().HaveMaxLength(40);
        builder.Properties<DeviceCredentialKind>().HaveConversion<string>().HaveMaxLength(40);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Personal data encrypted at rest (AES-GCM). Only columns never searched in SQL are encrypted.
        var encrypted = new ValueConverter<string, string>(v => protector.Encrypt(v), v => protector.Decrypt(v));

        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.Property(x => x.DisplayName).HasMaxLength(80).IsRequired();
            // Login identifier: must be searchable, so it is stored as international digits (not encrypted).
            e.Property(x => x.Msisdn).HasMaxLength(15).IsRequired();
            e.HasIndex(x => x.Msisdn).IsUnique();
            e.Property(x => x.PasscodeHash).HasMaxLength(200).IsRequired();
            e.Property(x => x.PreferredLanguage).HasMaxLength(8).IsRequired();
            e.Property(x => x.SpeechRate).HasPrecision(3, 2);
        });

        b.Entity<Device>(e =>
        {
            e.ToTable("devices");
            e.HasKey(x => x.Id);
            e.Property(x => x.InstallationId).HasMaxLength(100).IsRequired();
            e.Property(x => x.Platform).HasMaxLength(20).IsRequired();
            e.Property(x => x.Label).HasMaxLength(100);
            e.HasIndex(x => new { x.UserId, x.InstallationId }).IsUnique();
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<DeviceCredential>(e =>
        {
            e.ToTable("device_credentials");
            e.HasKey(x => x.Id);
            e.Property(x => x.SecretProtected).HasMaxLength(300).IsRequired();
            e.HasIndex(x => new { x.UserId, x.DeviceId });
            e.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Session>(e =>
        {
            e.ToTable("sessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.DeviceLabel).HasMaxLength(100);
            e.HasIndex(x => x.UserId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<AuthenticationAttempt>(e =>
        {
            e.ToTable("authentication_attempts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Reason).HasMaxLength(200).IsRequired();
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.RecipientMasked).HasMaxLength(15);
            e.HasIndex(x => new { x.UserId, x.OccurredAt });
            e.HasIndex(x => x.TransactionId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Transaction>().WithMany().HasForeignKey(x => x.TransactionId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<AuthenticationChallenge>(e =>
        {
            e.ToTable("authentication_challenges");
            e.HasKey(x => x.Id);
            e.Property(x => x.Nonce).HasMaxLength(64).IsRequired();
            e.Property(x => x.BindingFingerprint).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.TransactionId);
            e.HasOne<Transaction>().WithMany().HasForeignKey(x => x.TransactionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Contact>(e =>
        {
            e.ToTable("contacts");
            e.HasKey(x => x.Id);
            e.Property(x => x.DisplayName).HasMaxLength(80).IsRequired();
            e.Property(x => x.Aliases).HasMaxLength(300).IsRequired();
            e.Property(x => x.Msisdn).HasConversion(encrypted).HasMaxLength(200).IsRequired();
            e.Property(x => x.MsisdnMasked).HasMaxLength(15).IsRequired();
            e.Ignore(x => x.Number);
            e.Ignore(x => x.LastFour);
            e.Ignore(x => x.AliasList);
            e.HasIndex(x => x.UserId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Transaction>(e =>
        {
            e.ToTable("transactions");
            e.HasKey(x => x.Id);
            e.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
            e.Property(x => x.RequestFingerprint).HasMaxLength(64).IsRequired();
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            e.Property(x => x.RecipientName).HasMaxLength(80).IsRequired();
            e.Property(x => x.RecipientMsisdn).HasConversion(encrypted).HasMaxLength(200).IsRequired();
            e.Property(x => x.RecipientMasked).HasMaxLength(15).IsRequired();
            e.Property(x => x.Language).HasMaxLength(8).IsRequired();
            e.Property(x => x.Provider).HasMaxLength(40).IsRequired();
            e.Property(x => x.ProviderReference).HasMaxLength(64);
            e.Property(x => x.ProviderFinancialId).HasMaxLength(64);
            e.Property(x => x.AuthorizationFingerprint).HasMaxLength(64);
            e.Property(x => x.AuthenticationMethod).HasMaxLength(30);
            e.Property(x => x.FailureReason).HasMaxLength(120);
            // Optimistic concurrency: of two requests racing to submit, only one saves SUBMITTING.
            e.Property(x => x.Version).IsConcurrencyToken();
            e.Ignore(x => x.Amount);
            e.ToTable(t => t.HasCheckConstraint("ck_transactions_amount_positive", "amount_minor > 0"));

            // The client's idempotency key: the same request can never create two transfers.
            e.HasIndex(x => new { x.UserId, x.IdempotencyKey }).IsUnique();
            // MTN's X-Reference-Id: one provider submission per reference.
            e.HasIndex(x => new { x.Provider, x.ProviderReference }).IsUnique().HasFilter("provider_reference IS NOT NULL");
            e.HasIndex(x => new { x.State, x.UpdatedAt });
            e.HasIndex(x => new { x.UserId, x.CreatedAt });

            e.HasMany(x => x.Events).WithOne().HasForeignKey(x => x.TransactionId).OnDelete(DeleteBehavior.Restrict);
            e.Navigation(x => x.Events).HasField("_events").UsePropertyAccessMode(PropertyAccessMode.Field);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Contact>().WithMany().HasForeignKey(x => x.RecipientContactId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<TransactionEvent>(e =>
        {
            e.ToTable("transaction_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Actor).HasMaxLength(60).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(300).IsRequired();
            e.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
            e.HasIndex(x => new { x.TransactionId, x.Sequence }).IsUnique();
        });

        b.Entity<Consent>(e =>
        {
            e.ToTable("consents");
            e.HasKey(x => x.Id);
            e.Property(x => x.Provider).HasMaxLength(40).IsRequired();
            e.Property(x => x.Scope).HasMaxLength(80).IsRequired();
            e.Property(x => x.ProviderReference).HasMaxLength(100);
            e.HasIndex(x => x.TransactionId);
            e.HasOne<Transaction>().WithMany().HasForeignKey(x => x.TransactionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ProviderOperation>(e =>
        {
            e.ToTable("payment_provider_operations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Provider).HasMaxLength(40).IsRequired();
            e.Property(x => x.OperationType).HasMaxLength(40).IsRequired();
            e.Property(x => x.ExternalReference).HasMaxLength(64).IsRequired();
            e.Property(x => x.Status).HasMaxLength(40).IsRequired();
            e.Property(x => x.ResponseSummary).HasMaxLength(300);
            e.HasIndex(x => new { x.Provider, x.ExternalReference }).IsUnique();
            e.HasIndex(x => x.TransactionId);
            e.HasOne<Transaction>().WithMany().HasForeignKey(x => x.TransactionId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ReconciliationRecord>(e =>
        {
            e.ToTable("reconciliation_records");
            e.HasKey(x => x.Id);
            e.Property(x => x.KasaState).HasMaxLength(40).IsRequired();
            e.Property(x => x.ProviderStatus).HasMaxLength(40).IsRequired();
            e.Property(x => x.Resolution).HasMaxLength(60).IsRequired();
            e.Property(x => x.Notes).HasMaxLength(300).IsRequired();
            e.HasIndex(x => x.TransactionId);
            e.HasOne<Transaction>().WithMany().HasForeignKey(x => x.TransactionId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ProviderCallbackReceipt>(e =>
        {
            e.ToTable("provider_callback_receipts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Provider).HasMaxLength(40).IsRequired();
            e.Property(x => x.Fingerprint).HasMaxLength(64).IsRequired();
            e.Property(x => x.ProviderReference).HasMaxLength(64).IsRequired();
            e.Property(x => x.Summary).HasMaxLength(300).IsRequired();
            // A duplicate callback cannot be recorded twice, so it cannot be acted on twice.
            e.HasIndex(x => x.Fingerprint).IsUnique();
            e.HasOne<Transaction>().WithMany().HasForeignKey(x => x.TransactionId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<AuditEvent>(e =>
        {
            e.ToTable("audit_logs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Operation).HasMaxLength(60).IsRequired();
            e.Property(x => x.Result).HasMaxLength(30).IsRequired();
            e.Property(x => x.Detail).HasMaxLength(300);
            e.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
            e.HasIndex(x => new { x.UserId, x.OccurredAt });
            e.HasIndex(x => x.Operation);
        });

        b.Entity<Conversation>(e =>
        {
            e.ToTable("conversations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Language).HasMaxLength(8).IsRequired();
            e.Property(x => x.PendingRecipientQuery).HasMaxLength(80);
            e.Property(x => x.CandidateContactIds).HasMaxLength(400);
            e.Property(x => x.LastReplyKey).HasMaxLength(40);
            e.Property(x => x.LastReplyArgs).HasMaxLength(4000);
            e.Property(x => x.PreviousSnapshot).HasMaxLength(1000);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.Ignore(x => x.Candidates);
            e.HasIndex(x => new { x.UserId, x.UpdatedAt });
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Transaction>().WithMany().HasForeignKey(x => x.TransactionId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<ConversationTurn>(e =>
        {
            e.ToTable("conversation_turns");
            e.HasKey(x => x.Id);
            e.Property(x => x.Role).HasMaxLength(10).IsRequired();
            e.Property(x => x.Text).HasMaxLength(2000).IsRequired();
            e.Property(x => x.Language).HasMaxLength(8).IsRequired();
            e.Property(x => x.InputMode).HasMaxLength(10).IsRequired();
            e.Property(x => x.RecognizedBy).HasMaxLength(40);
            e.HasIndex(x => new { x.ConversationId, x.Sequence }).IsUnique();
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<IdempotencyRecord>(e =>
        {
            e.ToTable("idempotency_keys");
            e.HasKey(x => x.Id);
            e.Property(x => x.Scope).HasMaxLength(40).IsRequired();
            e.Property(x => x.Key).HasMaxLength(128).IsRequired();
            e.Property(x => x.RequestFingerprint).HasMaxLength(400).IsRequired();
            e.Property(x => x.ResponseJson).HasColumnType("jsonb").IsRequired();
            e.HasIndex(x => new { x.UserId, x.Scope, x.Key }).IsUnique();
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Notification>(e =>
        {
            e.ToTable("notifications");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasMaxLength(40).IsRequired();
            e.Property(x => x.Language).HasMaxLength(8).IsRequired();
            e.Property(x => x.Text).HasMaxLength(500).IsRequired();
            e.HasIndex(x => new { x.UserId, x.ReadAt });
            // One result notification per transfer, however many times the result is observed.
            e.HasIndex(x => new { x.TransactionId, x.Kind }).IsUnique().HasFilter("transaction_id IS NOT NULL");
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Transaction>().WithMany().HasForeignKey(x => x.TransactionId).OnDelete(DeleteBehavior.Restrict);
        });

        // Every id is assigned by the domain (Guid.NewGuid()), never by the database. Declaring that matters:
        // otherwise EF treats a new TransactionEvent found through Transaction.Events (it already has an id)
        // as an existing row and UPDATEs it instead of INSERTing it.
        foreach (var entity in b.Model.GetEntityTypes())
            entity.FindProperty("Id")?.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
    }
}
