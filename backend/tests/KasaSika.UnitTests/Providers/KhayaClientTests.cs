using System.Net;
using KasaSika.Application.Speech;
using KasaSika.Infrastructure.Speech.Khaya;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace KasaSika.UnitTests.Providers;

/// <summary>Khaya is mocked here; real keys are only used for explicit integration runs (docs/integrations/khaya.md).</summary>
public class KhayaClientTests
{
    private static (KhayaClient Client, FakeHttp Http) Create(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
    {
        var http = new FakeHttp(responses);
        var options = Options.Create(new KhayaOptions { SubscriptionKey = "primary-key", SecondarySubscriptionKey = "secondary-key" });
        return (new KhayaClient(new HttpClient(http) { BaseAddress = new Uri("https://developer-api.khaya.ai") }, options, NullLogger<KhayaClient>.Instance), http);
    }

    [Fact]
    public async Task Transcribe_sends_raw_audio_with_language_and_subscription_header()
    {
        var (client, http) = Create(FakeHttp.Json(HttpStatusCode.OK, """{"text": "Fahana si de aduonum ma Ama."}"""));
        var text = await client.TranscribeAsync([1, 2, 3], "audio/wav", "twi", default);

        Assert.Equal("Fahana si de aduonum ma Ama.", text);
        var (request, _) = Assert.Single(http.Requests);
        Assert.Equal("/asr/v3/transcribe?language=twi", request.RequestUri!.PathAndQuery);
        Assert.Equal("primary-key", request.Headers.GetValues("Ocp-Apim-Subscription-Key").Single());
        Assert.Equal("audio/wav", request.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Translation_uses_iso_639_3_pair_and_reads_a_bare_json_string()
    {
        var (client, http) = Create(FakeHttp.Json(HttpStatusCode.OK, "\"Wo ho te sɛn?\""));
        Assert.Equal("Wo ho te sɛn?", await client.TranslateAsync("Hello, how are you?", "eng", "twi", default));
        Assert.Contains("\"lang\":\"eng-twi\"", http.Requests[0].Body);
    }

    [Fact]
    public async Task Tts_returns_audio_bytes_and_type()
    {
        var (client, http) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([9, 9, 9]) { Headers = { ContentType = new("audio/mpeg") } } });
        var (audio, type) = await client.SynthesizeAsync("Wo ho te sɛn?", "twi", null, default);
        Assert.Equal(3, audio.Length);
        Assert.Equal("audio/mpeg", type);
        Assert.Contains("\"language\":\"twi\"", http.Requests[0].Body);
    }

    [Fact]
    public async Task Rejected_primary_key_falls_back_to_secondary_key()
    {
        var (client, http) = Create(
            FakeHttp.Json(HttpStatusCode.Unauthorized, """{"statusCode":401,"message":"Access denied"}"""),
            FakeHttp.Json(HttpStatusCode.OK, """{"text":"hello"}"""));
        Assert.Equal("hello", await client.TranscribeAsync([1], "audio/wav", "eng", default));
        Assert.Equal("secondary-key", http.Requests[1].Request.Headers.GetValues("Ocp-Apim-Subscription-Key").Single());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"code":"VALIDATION_FAILED","message":"x","details":[{"code":"INVALID_AUDIO_FORMAT","target":"audio","message":"x"}]}}""", SpeechFailure.InvalidAudio)]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"code":"VALIDATION_FAILED","message":"x","details":[{"code":"UNSUPPORTED_LANGUAGE","target":"language","message":"Use 'twi'"}]}}""", SpeechFailure.UnsupportedLanguage)]
    [InlineData(HttpStatusCode.TooManyRequests, "{}", SpeechFailure.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, """{"error":{"code":"SYSTEM_ERROR","message":"x","details":[]}}""", SpeechFailure.Unavailable)]
    [InlineData(HttpStatusCode.Forbidden, "{}", SpeechFailure.NotAuthorized)]
    public async Task Documented_errors_become_typed_failures(HttpStatusCode status, string body, SpeechFailure expected)
    {
        var (client, _) = Create(FakeHttp.Json(status, body));
        var ex = await Assert.ThrowsAsync<SpeechProviderException>(() => client.TranscribeAsync([1], "audio/wav", "twi", default));
        Assert.Equal(expected, ex.Failure);
    }

    [Fact]
    public async Task Both_keys_rejected_is_not_authorized()
    {
        var (client, _) = Create(FakeHttp.Status(HttpStatusCode.Unauthorized), FakeHttp.Status(HttpStatusCode.Unauthorized));
        var ex = await Assert.ThrowsAsync<SpeechProviderException>(() => client.TranscribeAsync([1], "audio/wav", "twi", default));
        Assert.Equal(SpeechFailure.NotAuthorized, ex.Failure);
    }

    [Fact]
    public async Task Timeout_and_network_failure_are_typed_never_a_fabricated_transcript()
    {
        var (timeoutClient, _) = Create(FakeHttp.Throws(new TaskCanceledException("timeout")));
        Assert.Equal(SpeechFailure.Timeout, (await Assert.ThrowsAsync<SpeechProviderException>(() => timeoutClient.TranscribeAsync([1], "audio/wav", "twi", default))).Failure);

        var (downClient, _) = Create(FakeHttp.Throws(new HttpRequestException("down")));
        Assert.Equal(SpeechFailure.Unavailable, (await Assert.ThrowsAsync<SpeechProviderException>(() => downClient.TranscribeAsync([1], "audio/wav", "twi", default))).Failure);
    }

    [Fact]
    public async Task Recognizer_refuses_formats_khaya_does_not_accept_before_calling_it()
    {
        var (client, http) = Create(FakeHttp.Json(HttpStatusCode.OK, """{"text":"x"}"""));
        var recognizer = new KhayaSpeechRecognitionProvider(client, Options.Create(new KhayaOptions { SubscriptionKey = "k" }));
        var twi = new LanguageInfo("tw", "Twi", "Twi", true, "twi", null, true, true);
        var ex = await Assert.ThrowsAsync<SpeechProviderException>(() => recognizer.TranscribeAsync(new AudioClip([1], "audio/aac"), twi, default));
        Assert.Equal(SpeechFailure.InvalidAudio, ex.Failure);
        Assert.Empty(http.Requests);
    }
}
