using System.Net;
using System.Text;

namespace KasaSika.UnitTests.Providers;

/// <summary>Scripted HTTP responses; records every request so tests can assert headers and bodies.</summary>
public sealed class FakeHttp(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
{
    private int _index;
    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request, body));
        var respond = responses[Math.Min(_index++, responses.Length - 1)];
        return respond(request);
    }

    public static Func<HttpRequestMessage, HttpResponseMessage> Json(HttpStatusCode status, string json) =>
        _ => new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static Func<HttpRequestMessage, HttpResponseMessage> Status(HttpStatusCode status) => _ => new HttpResponseMessage(status);

    public static Func<HttpRequestMessage, HttpResponseMessage> Throws(Exception ex) => _ => throw ex;
}
