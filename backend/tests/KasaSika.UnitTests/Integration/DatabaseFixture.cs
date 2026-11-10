using System.Security.Cryptography;
using KasaSika.Application;
using KasaSika.Application.Abstractions;
using KasaSika.Application.Demo;
using KasaSika.Domain;
using KasaSika.Domain.Payments;
using KasaSika.Domain.Transactions;
using KasaSika.Infrastructure;
using KasaSika.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KasaSika.UnitTests.Integration;

/// <summary>
/// Real PostgreSQL (the dev cluster from scripts/dev-postgres.ps1, database kasa_sika_test), because the
/// guarantees under test - unique indexes, optimistic concurrency - are enforced by the database.
/// Override with the KASA_TEST_DB environment variable.
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    public static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("KASA_TEST_DB") ?? "Host=127.0.0.1;Port=5433;Database=kasa_sika_test;Username=kasa_sika;Timeout=60;Command Timeout=300";

    public const string DemoPasscode = "246810";

    public IServiceProvider Build(Action<IServiceCollection>? configure = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:KasaSika"] = ConnectionString,
            ["Security:JwtSigningKey"] = Key(48),
            ["Security:FieldEncryptionKey"] = Key(32),
            ["Security:FingerprintKey"] = Key(32),
            ["Payments:Provider"] = "Mock",
            ["Khaya:CacheDirectory"] = Path.Combine(Path.GetTempPath(), "kasa-test-tts"),
            // Enables the Twi voice path. No test fetches audio, so Khaya is never called.
            ["Khaya:SubscriptionKey"] = "test-key-never-sent",
            ["Reconciliation:NotFoundGraceMinutes"] = "0",
            ["Demo:Enabled"] = "true",
            ["Demo:UserPhone"] = "0240000001",
            ["Demo:Passcode"] = DemoPasscode,
            ["Demo:Contacts:0:Name"] = "Ama Owusu", ["Demo:Contacts:0:Phone"] = "0240000111", ["Demo:Contacts:0:Scenario"] = "Success",
            ["Demo:Contacts:1:Name"] = "Kofi Mensah", ["Demo:Contacts:1:Phone"] = "0240000333", ["Demo:Contacts:1:Scenario"] = "Success",
            ["Demo:Contacts:2:Name"] = "Kofi Boateng", ["Demo:Contacts:2:Phone"] = "0240000444", ["Demo:Contacts:2:Scenario"] = "Success",
            ["Demo:Contacts:3:Name"] = "Yaw Darko", ["Demo:Contacts:3:Phone"] = "0200000666", ["Demo:Contacts:3:Scenario"] = "RecipientInvalid",
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddKasaApplication(config);
        services.AddKasaInfrastructure(config, Path.GetTempPath());
        services.RemoveAll<IHostedService>();
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    // One set of keys for the whole run, so data written by one provider instance is readable by the next.
    private static readonly Dictionary<int, string> Keys = new();
    private static string Key(int bytes)
    {
        lock (Keys) return Keys.TryGetValue(bytes, out var k) ? k : Keys[bytes] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes));
    }

    public async Task InitializeAsync()
    {
        using var scope = Build().CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KasaDbContext>();
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<DemoService>().SeedAsync(CancellationToken.None);
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

[CollectionDefinition("database")]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>;

internal static class ServiceCollectionTestExtensions
{
    public static void RemoveAll<T>(this IServiceCollection services)
    {
        foreach (var d in services.Where(d => d.ServiceType == typeof(T)).ToList()) services.Remove(d);
    }

    public static void ReplaceProvider(this IServiceCollection services, IPaymentProvider provider)
    {
        services.RemoveAll<IPaymentProvider>();
        services.AddSingleton(provider);
    }
}

/// <summary>A provider that counts submissions and can be scripted to be slow or uncertain.</summary>
public sealed class ScriptedProvider : IPaymentProvider
{
    private int _submissions;
    public int Submissions => _submissions;
    public TimeSpan SubmitDelay { get; set; }
    public SubmissionOutcome Outcome { get; set; } = SubmissionOutcome.Accepted;
    public ProviderStatusReport Status { get; set; } = new(ProviderTransferStatus.Pending);

    public string Name => "mock";
    public ProviderMode Mode => ProviderMode.DemoSimulated;
    public ProviderCapabilities Capabilities { get; } = new(false, false, "test");
    public Task<bool> IsAccountHolderActiveAsync(Msisdn recipient, CancellationToken ct) => Task.FromResult(true);
    public Task<ConsentOutcome> RequestConsentAsync(Guid transactionId, string scope, CancellationToken ct) =>
        Task.FromResult(new ConsentOutcome(ConsentStatus.NotRequired, null, null, "test"));

    public async Task<SubmissionResult> SubmitTransferAsync(TransferRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref _submissions);
        await Task.Delay(SubmitDelay, ct);
        return new SubmissionResult(Outcome, Outcome == SubmissionOutcome.Accepted ? null : "scripted");
    }

    public Task<ProviderStatusReport> GetTransferStatusAsync(string reference, CancellationToken ct) => Task.FromResult(Status);
    public Task<ProviderBalance> GetOwnAccountBalanceAsync(CancellationToken ct) => Task.FromResult(new ProviderBalance("0", "GHS"));
    public ProviderCallback ParseCallback(IReadOnlyDictionary<string, string> headers, string body) =>
        new(body.Trim(), Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body))), "test");
}
