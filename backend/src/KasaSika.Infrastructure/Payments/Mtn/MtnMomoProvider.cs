using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KasaSika.Application.Abstractions;
using KasaSika.Domain;
using KasaSika.Domain.Payments;
using KasaSika.Domain.Transactions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KasaSika.Infrastructure.Payments.Mtn;

/// <summary>
/// MTN MoMo credentials and routing. Secrets come only from user-secrets / environment variables
/// (Mtn__SubscriptionKey, Mtn__ApiUser, Mtn__ApiKey) and never reach the mobile app.
/// </summary>
public sealed class MtnOptions
{
    public const string Section = "Mtn";

    public string BaseUrl { get; set; } = "https://sandbox.momodeveloper.mtn.com";
    /// <summary>Disbursements product subscription key (Ocp-Apim-Subscription-Key).</summary>
    public string SubscriptionKey { get; set; } = "";
    public string ApiUser { get; set; } = "";
    public string ApiKey { get; set; } = "";
    /// <summary>X-Target-Environment. "sandbox" for the MTN sandbox.</summary>
    public string TargetEnvironment { get; set; } = "sandbox";
    /// <summary>
    /// Currency sent to MTN. The sandbox rejects local currencies with INVALID_CURRENCY and is commonly
    /// documented as EUR-only (REQUIRES_MTN_CONFIRMATION for Ghana production: GHS).
    /// </summary>
    public string Currency { get; set; } = "EUR";
    public int TimeoutSeconds { get; set; } = 20;

    public bool IsConfigured => SubscriptionKey.Length > 0 && ApiUser.Length > 0 && ApiKey.Length > 0;
}

/// <summary>
/// MTN MoMo Disbursements adapter. Owns every MTN HTTP call: access token, account-holder validation,
/// transfer, transfer status, balance and callback parsing. Contracts as verified on momodeveloper.mtn.com
/// (see docs/integrations/mtn.md).
///
/// IMPORTANT, and surfaced to users via <see cref="Capabilities"/>: Disbursements "Transfer" moves money FROM
/// the API user's own account (Kasa Sika's disbursement account) TO the payee. It does not debit the end
/// user's wallet. Debiting the user's own wallet requires MTN's consent-based flows (bc-authorize / CIBA),
/// which are REQUIRES_MTN_CONFIRMATION and not implemented here.
/// </summary>
public sealed class MtnMomoProvider(HttpClient http, MtnTokenCache tokens, IOptions<MtnOptions> options, ILogger<MtnMomoProvider> logger) : IPaymentProvider
{
    public const string ProviderName = "mtn";
    private static readonly HashSet<string> DefinitiveErrors =
        ["INVALID_CURRENCY", "PAYEE_NOT_FOUND", "NOT_ALLOWED_TARGET_ENVIRONMENT", "NOT_ALLOWED", "INVALID_CALLBACK_URL_HOST", "PAYER_NOT_FOUND", "NOT_ENOUGH_FUNDS"];

    private MtnOptions O => options.Value;

    public string Name => ProviderName;
    public ProviderMode Mode => O.TargetEnvironment == "sandbox" ? ProviderMode.Sandbox : ProviderMode.Live;
    public ProviderCapabilities Capabilities => new(false, false,
        $"MTN Disbursements: paid from Kasa Sika's own {(Mode == ProviderMode.Sandbox ? "sandbox " : "")}disbursement account, in {O.Currency}. Not the user's wallet.");

    public async Task<bool> IsAccountHolderActiveAsync(Msisdn recipient, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
            $"/disbursement/v1_0/accountholder/msisdn/{recipient.International}/active"), cancellationToken);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest) return false;
        if (!response.IsSuccessStatusCode) throw Unavailable(response);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        // Documented as "True if account holder is registered and active": accept {"result": true} or a bare true.
        if (string.IsNullOrWhiteSpace(body)) return true;
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Object when doc.RootElement.TryGetProperty("result", out var r) => r.ValueKind == JsonValueKind.True,
            _ => false,
        };
    }

    public Task<ConsentOutcome> RequestConsentAsync(Guid transactionId, string scope, CancellationToken cancellationToken) =>
        Task.FromResult(new ConsentOutcome(ConsentStatus.NotRequired, null, null,
            "MTN Disbursements pays from Kasa Sika's own account: no consumer consent applies. Debiting the user's own wallet via consent is REQUIRES_MTN_CONFIRMATION."));

    public async Task<SubmissionResult> SubmitTransferAsync(TransferRequest request, CancellationToken cancellationToken)
    {
        var payload = new
        {
            amount = FormatAmount(request.Amount.AmountMinor),
            currency = O.Currency,
            // externalId = our reference, so a callback (which carries externalId) maps back to the transaction.
            externalId = request.Reference,
            payee = new { partyIdType = "MSISDN", partyId = request.Recipient.International },
            payerMessage = request.Note,
            payeeNote = request.Note,
        };
        HttpResponseMessage response;
        try
        {
            response = await SendAsync(() =>
            {
                var msg = new HttpRequestMessage(HttpMethod.Post, "/disbursement/v1_0/transfer") { Content = JsonContent.Create(payload) };
                // X-Reference-Id is MTN's idempotency key: a retry with the same id cannot create a second transfer.
                msg.Headers.Add("X-Reference-Id", request.Reference);
                return msg;
            }, cancellationToken);
        }
        catch (ProviderUnavailableException ex)
        {
            // Timeout / connection lost / 5xx while sending: MTN may or may not have it.
            return SubmissionResult.Uncertain(ex.Message);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Accepted) return SubmissionResult.Accepted();
            var code = await ErrorCodeAsync(response, cancellationToken);
            // 409 RESOURCE_ALREADY_EXIST: MTN already has this reference (our earlier attempt got through).
            if (response.StatusCode == HttpStatusCode.Conflict && code == "RESOURCE_ALREADY_EXIST") return SubmissionResult.Accepted();
            if ((int)response.StatusCode is >= 400 and < 500) return SubmissionResult.Rejected($"MTN_{(int)response.StatusCode}:{code ?? "REJECTED"}");
            // MTN documents some definitive failures on 500 (e.g. INVALID_CURRENCY). Anything else on 5xx is uncertain.
            if (code is not null && DefinitiveErrors.Contains(code)) return SubmissionResult.Rejected($"MTN:{code}");
            return SubmissionResult.Uncertain($"MTN HTTP {(int)response.StatusCode}{(code is null ? "" : $" {code}")}");
        }
    }

    public async Task<ProviderStatusReport> GetTransferStatusAsync(string reference, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"/disbursement/v1_0/transfer/{Uri.EscapeDataString(reference)}"), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return new ProviderStatusReport(ProviderTransferStatus.NotFound);
        if (!response.IsSuccessStatusCode) throw Unavailable(response);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = doc.RootElement;
        var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
        var reason = root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.Object && r.TryGetProperty("code", out var rc) ? rc.GetString() : null;
        var financialId = root.TryGetProperty("financialTransactionId", out var f) ? f.ToString() : null;
        return status?.ToUpperInvariant() switch
        {
            "SUCCESSFUL" => new ProviderStatusReport(ProviderTransferStatus.Successful, null, financialId),
            "FAILED" => new ProviderStatusReport(ProviderTransferStatus.Failed, reason),
            "PENDING" => new ProviderStatusReport(ProviderTransferStatus.Pending),
            _ => new ProviderStatusReport(ProviderTransferStatus.Unrecognized),
        };
    }

    public async Task<ProviderBalance> GetOwnAccountBalanceAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "/disbursement/v1_0/account/balance"), cancellationToken);
        if (!response.IsSuccessStatusCode) throw Unavailable(response);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return new ProviderBalance(doc.RootElement.GetProperty("availableBalance").ToString(), doc.RootElement.GetProperty("currency").GetString() ?? "");
    }

    /// <summary>
    /// MTN posts the transfer result (with our externalId) to X-Callback-Url. MTN documents no signature, so
    /// only the reference is used: the status is then fetched from MTN over an authenticated call.
    /// </summary>
    public ProviderCallback ParseCallback(IReadOnlyDictionary<string, string> headers, string body)
    {
        string? reference = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            reference = root.TryGetProperty("referenceId", out var id) ? id.GetString()
                : root.TryGetProperty("externalId", out var ext) ? ext.ToString() : null;
        }
        catch (JsonException) { }
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 64 || !Guid.TryParse(reference, out _))
            throw KasaException.Validation("INVALID_CALLBACK", "The callback could not be read.");
        return new ProviderCallback(reference, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body))), "MTN transfer callback");
    }

    // ------------------------------------------------------------------------------------------ plumbing

    private static string FormatAmount(long minor) =>
        minor % 100 == 0
            ? (minor / 100).ToString(CultureInfo.InvariantCulture)
            : (minor / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Adds MTN's headers and a bearer token; refreshes the token once on 401. Transport failures become ProviderUnavailableException.</summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var request = build();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAsync(attempt > 0, ct));
            request.Headers.Add("X-Target-Environment", O.TargetEnvironment);
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "MTN request {Method} {Path} failed in transport", request.Method, request.RequestUri);
                throw new ProviderUnavailableException("Could not reach MTN.", ex);
            }
            finally
            {
                request.Dispose();
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                response.Dispose();
                continue;
            }
            if ((int)response.StatusCode >= 500 && response.StatusCode != HttpStatusCode.InternalServerError)
            {
                var status = (int)response.StatusCode;
                response.Dispose();
                throw new ProviderUnavailableException($"MTN returned HTTP {status}.");
            }
            return response;
        }
    }

    private static ProviderUnavailableException Unavailable(HttpResponseMessage response) =>
        new($"MTN returned HTTP {(int)response.StatusCode}.");

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(body)) return null;
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// Caches the Disbursements access token (POST /disbursement/token/, Basic apiUser:apiKey) until shortly
/// before it expires. One refresh at a time.
/// </summary>
public sealed class MtnTokenCache(IHttpClientFactory httpClients, IOptions<MtnOptions> options, TimeProvider clock)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt;

    public const string HttpClientName = "mtn-token";

    public async Task<string> GetAsync(bool forceRefresh, CancellationToken ct)
    {
        if (!forceRefresh && _token is not null && clock.GetUtcNow() < _expiresAt) return _token;
        await _gate.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _token is not null && clock.GetUtcNow() < _expiresAt) return _token;
            var o = options.Value;
            using var request = new HttpRequestMessage(HttpMethod.Post, "/disbursement/token/");
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{o.ApiUser}:{o.ApiKey}")));
            HttpResponseMessage response;
            try
            {
                response = await httpClients.CreateClient(HttpClientName).SendAsync(request, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new ProviderUnavailableException("Could not reach MTN to obtain an access token.", ex);
            }
            using (response)
            {
                if (!response.IsSuccessStatusCode)
                    throw new ProviderUnavailableException($"MTN refused the access token request (HTTP {(int)response.StatusCode}). Check Mtn:ApiUser/ApiKey/SubscriptionKey.");
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                _token = doc.RootElement.GetProperty("access_token").GetString();
                var lifetime = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var secs) ? secs : 3600;
                _expiresAt = clock.GetUtcNow().AddSeconds(Math.Max(60, lifetime - 60));
                return _token!;
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
