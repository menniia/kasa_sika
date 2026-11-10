using System.Net;
using KasaSika.Application.Abstractions;
using KasaSika.Domain;
using KasaSika.Domain.Transactions;
using KasaSika.Infrastructure.Payments.Mtn;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace KasaSika.UnitTests.Providers;

public class MtnMomoProviderTests
{
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("https://sandbox.momodeveloper.mtn.com") };
    }

    private static (MtnMomoProvider Provider, FakeHttp Api) Create(params Func<HttpRequestMessage, HttpResponseMessage>[] apiResponses)
    {
        var options = Options.Create(new MtnOptions { SubscriptionKey = "sub", ApiUser = "user", ApiKey = "key", Currency = "EUR" });
        var tokenHttp = new FakeHttp(FakeHttp.Json(HttpStatusCode.OK, """{"access_token":"tok","token_type":"access_token","expires_in":3600}"""));
        var tokens = new MtnTokenCache(new Factory(tokenHttp), options, TimeProvider.System);
        var api = new FakeHttp(apiResponses);
        var provider = new MtnMomoProvider(new HttpClient(api) { BaseAddress = new Uri("https://sandbox.momodeveloper.mtn.com") }, tokens, options, NullLogger<MtnMomoProvider>.Instance);
        return (provider, api);
    }

    private static TransferRequest Request(string reference) =>
        new(Guid.Parse(reference), reference, Money.FromMinor(5025, "GHS"), Msisdn.Parse("0240000111"), "Kasa Sika transfer");

    private const string Ref = "7c6b9a60-3f37-4e53-8c1c-7b8a4e2a1f10";

    [Fact]
    public async Task Transfer_sends_reference_as_idempotency_key_and_202_is_accepted_not_success()
    {
        var (provider, api) = Create(FakeHttp.Status(HttpStatusCode.Accepted));
        var result = await provider.SubmitTransferAsync(Request(Ref), default);

        Assert.Equal(SubmissionOutcome.Accepted, result.Outcome);
        var (req, body) = Assert.Single(api.Requests);
        Assert.Equal("/disbursement/v1_0/transfer", req.RequestUri!.AbsolutePath);
        Assert.Equal(Ref, req.Headers.GetValues("X-Reference-Id").Single());
        Assert.Equal("sandbox", req.Headers.GetValues("X-Target-Environment").Single());
        Assert.Equal("Bearer tok", req.Headers.Authorization!.ToString());
        Assert.Contains("\"amount\":\"50.25\"", body);
        Assert.Contains("\"partyId\":\"233240000111\"", body);
        Assert.Contains($"\"externalId\":\"{Ref}\"", body);
    }

    [Fact]
    public async Task Duplicate_reference_409_means_mtn_already_has_it_so_nothing_is_sent_twice()
    {
        var (provider, _) = Create(FakeHttp.Json(HttpStatusCode.Conflict, """{"code":"RESOURCE_ALREADY_EXIST","message":"Duplicated reference id."}"""));
        Assert.Equal(SubmissionOutcome.Accepted, (await provider.SubmitTransferAsync(Request(Ref), default)).Outcome);
    }

    [Fact]
    public async Task Documented_definitive_error_on_500_is_rejected()
    {
        var (provider, _) = Create(FakeHttp.Json(HttpStatusCode.InternalServerError, """{"code":"INVALID_CURRENCY","message":"Currency not supported."}"""));
        var result = await provider.SubmitTransferAsync(Request(Ref), default);
        Assert.Equal(SubmissionOutcome.Rejected, result.Outcome);
        Assert.Equal("MTN:INVALID_CURRENCY", result.Reason);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task Server_errors_without_a_definitive_code_are_uncertain_never_failed(HttpStatusCode status)
    {
        var (provider, _) = Create(FakeHttp.Status(status));
        Assert.Equal(SubmissionOutcome.Uncertain, (await provider.SubmitTransferAsync(Request(Ref), default)).Outcome);
    }

    [Fact]
    public async Task A_timeout_while_submitting_is_uncertain()
    {
        var (provider, _) = Create(FakeHttp.Throws(new TaskCanceledException("timeout")));
        Assert.Equal(SubmissionOutcome.Uncertain, (await provider.SubmitTransferAsync(Request(Ref), default)).Outcome);
    }

    [Theory]
    [InlineData("""{"status":"SUCCESSFUL","financialTransactionId":"363440463","externalId":"x"}""", ProviderTransferStatus.Successful)]
    [InlineData("""{"status":"FAILED","reason":{"code":"NOT_ENOUGH_FUNDS","message":"x"}}""", ProviderTransferStatus.Failed)]
    [InlineData("""{"status":"PENDING"}""", ProviderTransferStatus.Pending)]
    [InlineData("""{"status":"SOMETHING_NEW"}""", ProviderTransferStatus.Unrecognized)]
    public async Task Transfer_status_maps_mtn_statuses(string body, ProviderTransferStatus expected)
    {
        var (provider, api) = Create(FakeHttp.Json(HttpStatusCode.OK, body));
        var report = await provider.GetTransferStatusAsync(Ref, default);
        Assert.Equal(expected, report.Status);
        Assert.Equal($"/disbursement/v1_0/transfer/{Ref}", api.Requests[0].Request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Unknown_reference_is_not_found()
    {
        var (provider, _) = Create(FakeHttp.Json(HttpStatusCode.NotFound, """{"code":"RESOURCE_NOT_FOUND","message":"x"}"""));
        Assert.Equal(ProviderTransferStatus.NotFound, (await provider.GetTransferStatusAsync(Ref, default)).Status);
    }

    [Fact]
    public async Task A_failed_status_check_is_an_outage_not_an_answer()
    {
        var (provider, _) = Create(FakeHttp.Status(HttpStatusCode.ServiceUnavailable));
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.GetTransferStatusAsync(Ref, default));
    }

    [Theory]
    [InlineData("""{"result":true}""", true)]
    [InlineData("""{"result":false}""", false)]
    [InlineData("true", true)]
    public async Task Account_holder_validation(string body, bool expected)
    {
        var (provider, api) = Create(FakeHttp.Json(HttpStatusCode.OK, body));
        Assert.Equal(expected, await provider.IsAccountHolderActiveAsync(Msisdn.Parse("0240000111"), default));
        Assert.Equal("/disbursement/v1_0/accountholder/msisdn/233240000111/active", api.Requests[0].Request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Expired_token_is_refreshed_once_on_401()
    {
        var (provider, api) = Create(FakeHttp.Status(HttpStatusCode.Unauthorized), FakeHttp.Json(HttpStatusCode.OK, """{"status":"PENDING"}"""));
        Assert.Equal(ProviderTransferStatus.Pending, (await provider.GetTransferStatusAsync(Ref, default)).Status);
        Assert.Equal(2, api.Requests.Count);
    }

    [Fact]
    public void Callback_uses_only_the_reference_never_the_claimed_status()
    {
        var (provider, _) = Create();
        var callback = provider.ParseCallback(new Dictionary<string, string>(), $$"""{"externalId":"{{Ref}}","status":"SUCCESSFUL"}""");
        Assert.Equal(Ref, callback.ProviderReference);
        Assert.Throws<KasaException>(() => provider.ParseCallback(new Dictionary<string, string>(), """{"status":"SUCCESSFUL"}"""));
    }

    [Fact]
    public void Capabilities_say_honestly_that_disbursements_do_not_debit_the_user()
    {
        var (provider, _) = Create();
        Assert.False(provider.Capabilities.DebitsUserWallet);
        Assert.False(provider.Capabilities.UserWalletBalance);
        Assert.Equal(ProviderMode.Sandbox, provider.Mode);
    }
}
