using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KasaSika.Application.Abstractions;
using KasaSika.Application.Options;
using KasaSika.Domain;
using KasaSika.Domain.Payments;
using KasaSika.Domain.Transactions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KasaSika.Infrastructure.Payments;

/// <summary>
/// A stand-in for MTN with NO network and NO money. It keeps its own record of the references it was given
/// and answers status checks from it, exactly as MTN would. Everything else in Kasa Sika - the state machine,
/// idempotency, callbacks, reconciliation - runs unchanged, so a demo on the mock exercises the real software.
///
/// Scenario per recipient (configured in Demo:Contacts), or one override for everyone from the presenter panel:
///   Success           accepted (202), then SUCCESSFUL after a short delay; one callback
///   Pending           accepted, stays PENDING
///   Failure           accepted, then FAILED (NOT_ENOUGH_FUNDS); one callback
///   Timeout           the submit call "times out" (we cannot know if it arrived) but it DID arrive;
///                     reconciliation later finds it SUCCESSFUL - the case that must never be retried blindly
///   Unknown           accepted, then an unrecognisable status forever → UNKNOWN, never success/failure
///   DuplicateCallback accepted, SUCCESSFUL, and the same callback delivered twice
///   RecipientInvalid  the recipient is not an active account → validation fails, nothing submitted
/// </summary>
public sealed class MockPaymentProvider(
    IHttpClientFactory httpClients,
    IOptions<DemoOptions> demo,
    IOptions<PaymentsOptions> payments,
    TimeProvider clock,
    ILogger<MockPaymentProvider> logger) : IPaymentProvider, IDemoPaymentControls
{
    public const string ProviderName = "mock";
    private static readonly TimeSpan SettleAfter = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan NetworkDelay = TimeSpan.FromMilliseconds(250);

    private readonly ConcurrentDictionary<string, MockTransfer> _transfers = new();
    private volatile string? _override;

    private sealed record MockTransfer(string Reference, string Scenario, DateTimeOffset ReceivedAt, long AmountMinor, string FinancialId);

    public string Name => ProviderName;
    public ProviderMode Mode => ProviderMode.DemoSimulated;
    public ProviderCapabilities Capabilities { get; } = new(false, false, "Simulated. No money exists or moves.");

    public IReadOnlyList<string> Scenarios { get; } = ["Success", "Pending", "Failure", "Timeout", "Unknown", "DuplicateCallback", "RecipientInvalid"];
    public string? ActiveOverride => _override;

    public void SetOverride(string? scenario) =>
        _override = scenario is null ? null : Scenarios.First(s => s.Equals(scenario, StringComparison.OrdinalIgnoreCase));

    public string ScenarioFor(Msisdn recipient)
    {
        if (_override is { } o) return o;
        var contact = demo.Value.Contacts.FirstOrDefault(c => Msisdn.TryParse(c.Phone, out var m) && m!.International == recipient.International);
        return contact?.Scenario is { } s && Scenarios.Contains(s, StringComparer.OrdinalIgnoreCase)
            ? Scenarios.First(x => x.Equals(s, StringComparison.OrdinalIgnoreCase))
            : "Success";
    }

    public void Reset()
    {
        _transfers.Clear();
        _override = null;
    }

    public async Task<bool> IsAccountHolderActiveAsync(Msisdn recipient, CancellationToken cancellationToken)
    {
        await Task.Delay(NetworkDelay, cancellationToken);
        return ScenarioFor(recipient) != "RecipientInvalid";
    }

    public Task<ConsentOutcome> RequestConsentAsync(Guid transactionId, string scope, CancellationToken cancellationToken) =>
        Task.FromResult(new ConsentOutcome(ConsentStatus.NotRequired, null, null,
            "Mock provider: no consumer-consent step is simulated (see MTN capability matrix)."));

    public async Task<SubmissionResult> SubmitTransferAsync(TransferRequest request, CancellationToken cancellationToken)
    {
        await Task.Delay(NetworkDelay, cancellationToken);
        var scenario = ScenarioFor(request.Recipient);
        var transfer = new MockTransfer(request.Reference, scenario, clock.GetUtcNow(), request.Amount.AmountMinor,
            RandomNumberGenerator.GetInt32(100_000_000, 999_999_999).ToString());

        // Same reference twice is MTN's 409 RESOURCE_ALREADY_EXIST: the first request stands, nothing is duplicated.
        if (!_transfers.TryAdd(request.Reference, transfer)) return SubmissionResult.Accepted();

        if (scenario is "Success" or "Failure" or "DuplicateCallback")
            _ = DeliverCallbacksAsync(transfer, scenario == "DuplicateCallback" ? 2 : 1);

        return scenario == "Timeout"
            ? SubmissionResult.Uncertain("simulated timeout: no response from provider")
            : SubmissionResult.Accepted();
    }

    public Task<ProviderStatusReport> GetTransferStatusAsync(string reference, CancellationToken cancellationToken)
    {
        if (!_transfers.TryGetValue(reference, out var t))
            return Task.FromResult(new ProviderStatusReport(ProviderTransferStatus.NotFound));
        if (clock.GetUtcNow() - t.ReceivedAt < SettleAfter && t.Scenario != "Unknown")
            return Task.FromResult(new ProviderStatusReport(ProviderTransferStatus.Pending));
        return Task.FromResult(t.Scenario switch
        {
            "Success" or "DuplicateCallback" or "Timeout" => new ProviderStatusReport(ProviderTransferStatus.Successful, null, t.FinancialId),
            "Failure" => new ProviderStatusReport(ProviderTransferStatus.Failed, "NOT_ENOUGH_FUNDS"),
            "Pending" => new ProviderStatusReport(ProviderTransferStatus.Pending),
            _ => new ProviderStatusReport(ProviderTransferStatus.Unrecognized),
        });
    }

    public Task<ProviderBalance> GetOwnAccountBalanceAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ProviderBalance("0.00", "GHS"));

    /// <summary>Callback body shaped like MTN's transfer result; only the reference is ever used.</summary>
    public ProviderCallback ParseCallback(IReadOnlyDictionary<string, string> headers, string body)
    {
        string? reference = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("externalId", out var ext)) reference = ext.GetString();
        }
        catch (JsonException) { }
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 64)
            throw KasaException.Validation("INVALID_CALLBACK", "The callback could not be read.");
        return new ProviderCallback(reference, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body))), "mock transfer callback");
    }

    private async Task DeliverCallbacksAsync(MockTransfer transfer, int times)
    {
        var baseUrl = payments.Value.CallbackBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl)) return;
        try
        {
            await Task.Delay(SettleAfter + TimeSpan.FromMilliseconds(200));
            var body = JsonSerializer.Serialize(new
            {
                financialTransactionId = transfer.Scenario == "Failure" ? null : transfer.FinancialId,
                externalId = transfer.Reference,
                amount = (transfer.AmountMinor / 100m).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                currency = "GHS",
                status = transfer.Scenario == "Failure" ? "FAILED" : "SUCCESSFUL",
            });
            var client = httpClients.CreateClient(nameof(MockPaymentProvider));
            for (var i = 0; i < times; i++)
            {
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = await client.PostAsync($"{baseUrl.TrimEnd('/')}/api/v1/callbacks/{ProviderName}", content);
                logger.LogInformation("Mock callback {Attempt}/{Times} for {Reference}: HTTP {Status}", i + 1, times, transfer.Reference, (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            // A lost callback is normal in real life too; status polling and reconciliation still resolve it.
            logger.LogWarning(ex, "Mock callback delivery failed for {Reference}", transfer.Reference);
        }
    }
}
