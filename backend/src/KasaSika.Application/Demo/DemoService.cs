using KasaSika.Application.Abstractions;
using KasaSika.Application.Options;
using KasaSika.Domain;
using KasaSika.Domain.Contacts;
using KasaSika.Domain.Identity;
using KasaSika.Domain.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KasaSika.Application.Demo;

public sealed record DemoContactView(string Name, string MaskedPhone, string Scenario);

public sealed record DemoStateView(bool Enabled, string? ActiveOverride, IReadOnlyList<string> Scenarios, IReadOnlyList<DemoContactView> Contacts);

/// <summary>
/// Presenter controls: seed the demo account, choose mock outcomes, and reset between demonstrations.
/// Everything here touches ONLY users flagged <see cref="User.IsDemo"/>; the schema and any real user's data
/// are never modified. The API refuses to enable demo mode with a live payment provider.
/// </summary>
public sealed class DemoService(
    IKasaDbContext db,
    IPasscodeHasher hasher,
    IDemoPaymentControls? controls,
    TimeProvider clock,
    IOptions<DemoOptions> options,
    ILogger<DemoService> logger)
{
    private readonly DemoOptions _options = options.Value;

    public async Task SeedAsync(CancellationToken ct)
    {
        if (!_options.Enabled) return;
        if (_options.Passcode is not { Length: 6 })
        {
            logger.LogWarning("Demo mode is on but Demo:Passcode is not a 6-digit value; the demo account was not created.");
            return;
        }
        var now = clock.GetUtcNow();
        var msisdn = Msisdn.Parse(_options.UserPhone);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Msisdn == msisdn.International, ct);
        if (user is null)
        {
            user = User.Create(_options.UserName, msisdn, hasher.Hash(_options.Passcode), now, isDemo: true);
            db.Users.Add(user);
            logger.LogInformation("Created demo account {Masked}", msisdn.Masked);
        }
        else if (!user.IsDemo)
        {
            logger.LogWarning("Demo:UserPhone belongs to a non-demo account; not seeding demo data.");
            return;
        }

        var existing = await db.Contacts.Where(c => c.UserId == user.Id).ToListAsync(ct);
        foreach (var contact in _options.Contacts)
        {
            if (!Msisdn.TryParse(contact.Phone, out var phone)) continue;
            if (existing.Any(c => c.DisplayName == contact.Name)) continue;
            db.Contacts.Add(Contact.Create(user.Id, contact.Name, phone!, contact.Aliases, now));
        }
        await db.SaveChangesAsync(ct);
    }

    public DemoStateView State() => new(
        _options.Enabled,
        controls?.ActiveOverride,
        controls?.Scenarios ?? [],
        _options.Contacts.Select(c => new DemoContactView(c.Name, Msisdn.TryParse(c.Phone, out var m) ? m!.Masked : c.Phone,
            controls is not null && m is not null ? controls.ScenarioFor(m) : c.Scenario)).ToList());

    public void SetScenario(string? scenario)
    {
        EnsureEnabled();
        if (controls is null) throw KasaException.Validation("SCENARIOS_UNAVAILABLE", "Scenarios only apply to the mock payment provider.");
        if (scenario is not null && !controls.Scenarios.Contains(scenario, StringComparer.OrdinalIgnoreCase))
            throw KasaException.Validation("UNKNOWN_SCENARIO", $"Unknown scenario. Use one of: {string.Join(", ", controls.Scenarios)}.");
        controls.SetOverride(scenario);
    }

    /// <summary>
    /// Clears the demo account's transactions, conversations, authentication history and notifications, resets
    /// lockout and frequent-contact counters, and clears the mock provider. Leaves an audit entry.
    /// </summary>
    public async Task<int> ResetAsync(Guid requestedBy, string correlationId, CancellationToken ct)
    {
        EnsureEnabled();
        var demoUserIds = await db.Users.Where(u => u.IsDemo).Select(u => u.Id).ToListAsync(ct);
        if (!demoUserIds.Contains(requestedBy))
            throw KasaException.Forbidden("DEMO_ONLY", "Only a demo account can reset the demo.");

        var txIds = await db.Transactions.Where(t => demoUserIds.Contains(t.UserId)).Select(t => t.Id).ToListAsync(ct);
        var conversationIds = await db.Conversations.Where(c => demoUserIds.Contains(c.UserId)).Select(c => c.Id).ToListAsync(ct);

        // Children first; ExecuteDelete runs straight SQL scoped by the ids above.
        await db.TransactionEvents.Where(e => txIds.Contains(e.TransactionId)).ExecuteDeleteAsync(ct);
        await db.ProviderOperations.Where(o => txIds.Contains(o.TransactionId)).ExecuteDeleteAsync(ct);
        await db.Consents.Where(c => txIds.Contains(c.TransactionId)).ExecuteDeleteAsync(ct);
        await db.ReconciliationRecords.Where(r => txIds.Contains(r.TransactionId)).ExecuteDeleteAsync(ct);
        await db.CallbackReceipts.Where(r => r.TransactionId != null && txIds.Contains(r.TransactionId.Value)).ExecuteDeleteAsync(ct);
        await db.AuthenticationChallenges.Where(c => demoUserIds.Contains(c.UserId)).ExecuteDeleteAsync(ct);
        await db.AuthenticationAttempts.Where(a => a.UserId != null && demoUserIds.Contains(a.UserId.Value)).ExecuteDeleteAsync(ct);
        await db.Notifications.Where(n => demoUserIds.Contains(n.UserId)).ExecuteDeleteAsync(ct);
        await db.ConversationTurns.Where(t => conversationIds.Contains(t.ConversationId)).ExecuteDeleteAsync(ct);
        await db.Conversations.Where(c => demoUserIds.Contains(c.UserId)).ExecuteDeleteAsync(ct);
        await db.IdempotencyRecords.Where(r => demoUserIds.Contains(r.UserId)).ExecuteDeleteAsync(ct);
        await db.Transactions.Where(t => demoUserIds.Contains(t.UserId)).ExecuteDeleteAsync(ct);

        foreach (var user in await db.Users.Where(u => u.IsDemo).ToListAsync(ct)) user.ClearLockout();
        foreach (var contact in await db.Contacts.Where(c => demoUserIds.Contains(c.UserId)).ToListAsync(ct)) contact.ResetUsage();
        db.AuditEvents.Add(new AuditEvent(requestedBy, null, "DEMO_RESET", "OK", $"{txIds.Count} demo transactions cleared", correlationId, clock.GetUtcNow()));
        await db.SaveChangesAsync(ct);
        controls?.Reset();
        return txIds.Count;
    }

    private void EnsureEnabled()
    {
        if (!_options.Enabled) throw KasaException.Forbidden("DEMO_DISABLED", "Demo controls are not enabled on this server.");
    }
}
