using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace KasaSika.Infrastructure.Payments.Mtn;

/// <summary>
/// SANDBOX ONLY. Creates an API user and API key with the Sandbox User Provisioning API
/// (POST /v1_0/apiuser, POST /v1_0/apiuser/{X-Reference-Id}/apikey), using only the Disbursements
/// subscription key. In production, MTN issues these credentials through the partner portal instead.
/// Run with: dotnet run --project src/KasaSika.Api -- provision-mtn-sandbox [callbackHost]
/// </summary>
public sealed class MtnSandboxProvisioner(IHttpClientFactory httpClients, IOptions<MtnOptions> options)
{
    public async Task<(string ApiUser, string ApiKey)> ProvisionAsync(string callbackHost, CancellationToken ct)
    {
        if (options.Value.TargetEnvironment != "sandbox")
            throw new InvalidOperationException("Provisioning is only available for the MTN sandbox.");
        var client = httpClients.CreateClient(MtnTokenCache.HttpClientName);
        var apiUser = Guid.NewGuid().ToString();

        using (var create = new HttpRequestMessage(HttpMethod.Post, "/v1_0/apiuser") { Content = JsonContent.Create(new { providerCallbackHost = callbackHost }) })
        {
            create.Headers.Add("X-Reference-Id", apiUser);
            using var response = await client.SendAsync(create, ct);
            if (response.StatusCode != HttpStatusCode.Created)
                throw new InvalidOperationException($"MTN sandbox refused to create the API user (HTTP {(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(ct)}");
        }

        using var keyResponse = await client.PostAsync($"/v1_0/apiuser/{apiUser}/apikey", null, ct);
        if (keyResponse.StatusCode != HttpStatusCode.Created)
            throw new InvalidOperationException($"MTN sandbox refused to create the API key (HTTP {(int)keyResponse.StatusCode}).");
        using var doc = JsonDocument.Parse(await keyResponse.Content.ReadAsStringAsync(ct));
        return (apiUser, doc.RootElement.GetProperty("apiKey").GetString()!);
    }
}
