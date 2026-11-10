using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KasaSika.Application.Speech;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KasaSika.Infrastructure.Speech.Khaya;

/// <summary>
/// Khaya AI settings. Endpoints and header verified against developer.khaya.ai on 2026-09-22 (see
/// docs/integrations/khaya.md). Keys live only on the backend (user-secrets / environment:
/// Khaya__SubscriptionKey, Khaya__SecondarySubscriptionKey) and are never sent to the phone.
/// </summary>
public sealed class KhayaOptions
{
    public const string Section = "Khaya";

    /// <summary>API gateway. Alias: https://translation-api.ghananlp.org</summary>
    public string BaseUrl { get; set; } = "https://developer-api.khaya.ai";
    public string SubscriptionKey { get; set; } = "";
    /// <summary>Used when the primary key is rejected (401), so keys can be rotated without downtime.</summary>
    public string SecondarySubscriptionKey { get; set; } = "";
    /// <summary>ASR v3. Language is a query parameter; body is raw audio (wav, mp3, flac, ogg).</summary>
    public string AsrPath { get; set; } = "/asr/v3/transcribe";
    /// <summary>TTS v2. JSON body {text, language, speaker_id?, format?}.</summary>
    public string TtsPath { get; set; } = "/tts/v2/synthesize";
    /// <summary>Translation v2. JSON body {in, lang: "eng-twi"}; response is a JSON string.</summary>
    public string TranslatePath { get; set; } = "/v2/translate";
    /// <summary>male_low | male_high | female (GET /tts/v2/speakers).</summary>
    public string Voice { get; set; } = "female";
    /// <summary>wav | mp3 | ogg. MP3 plays everywhere (Khaya's WAV is 32-bit float, which some Android players reject).</summary>
    public string AudioFormat { get; set; } = "mp3";
    public int TimeoutSeconds { get; set; } = 30;
    /// <summary>Directory for cached synthesized replies (relative to the API's content root).</summary>
    public string CacheDirectory { get; set; } = ".cache/tts";

    public bool IsConfigured => SubscriptionKey.Length > 0 || SecondarySubscriptionKey.Length > 0;
}

/// <summary>
/// The only class that talks HTTP to Khaya. Maps Khaya's documented errors (400 VALIDATION_FAILED with
/// detail codes, 401 from the gateway, 429, 5xx SYSTEM_ERROR) to <see cref="SpeechFailure"/>.
/// </summary>
public sealed class KhayaClient(HttpClient http, IOptions<KhayaOptions> options, ILogger<KhayaClient> logger)
{
    private KhayaOptions O => options.Value;

    public async Task<string> TranscribeAsync(byte[] audio, string contentType, string languageCode, CancellationToken ct)
    {
        using var response = await SendAsync(() =>
        {
            var content = new ByteArrayContent(audio);
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            return new HttpRequestMessage(HttpMethod.Post, $"{O.AsrPath}?language={Uri.EscapeDataString(languageCode)}") { Content = content };
        }, ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "";
    }

    public async Task<string> TranslateAsync(string text, string from, string to, CancellationToken ct)
    {
        if (text.Length > 1000) throw new SpeechProviderException(SpeechFailure.InvalidInput, "Khaya translation accepts at most 1000 characters.");
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, O.TranslatePath)
        {
            Content = JsonContent.Create(new Dictionary<string, string> { ["in"] = text, ["lang"] = $"{from}-{to}" }),
        }, ct);
        // Documented response: a bare JSON string.
        return JsonSerializer.Deserialize<string>(await response.Content.ReadAsStringAsync(ct)) ?? "";
    }

    public async Task<(byte[] Audio, string ContentType)> SynthesizeAsync(string text, string languageCode, string? voice, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, O.TtsPath)
        {
            Content = JsonContent.Create(new Dictionary<string, object>
            {
                ["text"] = text,
                ["language"] = languageCode,
                ["speaker_id"] = voice ?? O.Voice,
                ["format"] = O.AudioFormat,
                ["stream"] = false,
            }),
        }, ct);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length == 0) throw new SpeechProviderException(SpeechFailure.Unavailable, "Khaya returned empty audio.");
        var type = response.Content.Headers.ContentType?.MediaType ?? (O.AudioFormat == "mp3" ? "audio/mpeg" : $"audio/{O.AudioFormat}");
        return (bytes, type == "audio/mp3" ? "audio/mpeg" : type);
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, CancellationToken ct)
    {
        var keys = new[] { O.SubscriptionKey, O.SecondarySubscriptionKey }.Where(k => k.Length > 0).ToArray();
        if (keys.Length == 0) throw new SpeechProviderException(SpeechFailure.NotAuthorized, "Khaya is not configured (no subscription key).");

        for (var i = 0; i < keys.Length; i++)
        {
            using var request = build();
            request.Headers.Add("Ocp-Apim-Subscription-Key", keys[i]);
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new SpeechProviderException(SpeechFailure.Timeout, "Khaya did not respond in time.", ex);
            }
            catch (HttpRequestException ex)
            {
                throw new SpeechProviderException(SpeechFailure.Unavailable, "Could not reach Khaya.", ex);
            }

            if (response.IsSuccessStatusCode) return response;

            var status = response.StatusCode;
            var detail = await DetailCodeAsync(response, ct);
            response.Dispose();
            if (status == HttpStatusCode.Unauthorized && i + 1 < keys.Length)
            {
                logger.LogWarning("Khaya rejected the primary subscription key; retrying with the secondary key.");
                continue;
            }
            throw status switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    new SpeechProviderException(SpeechFailure.NotAuthorized, "Khaya rejected the subscription key or the quota is exhausted."),
                HttpStatusCode.TooManyRequests => new SpeechProviderException(SpeechFailure.RateLimited, "Khaya rate limit reached."),
                HttpStatusCode.BadRequest => detail switch
                {
                    "INVALID_AUDIO_FORMAT" or "EMPTY_AUDIO" => new SpeechProviderException(SpeechFailure.InvalidAudio, $"Khaya could not read the audio ({detail})."),
                    "UNSUPPORTED_LANGUAGE" or "UNSUPPORTED_LANGUAGE_CODE" or "UNSUPPORTED_LANGUAGE_PAIR" =>
                        new SpeechProviderException(SpeechFailure.UnsupportedLanguage, $"Khaya does not support that language ({detail})."),
                    _ => new SpeechProviderException(SpeechFailure.InvalidInput, $"Khaya rejected the request ({detail ?? "VALIDATION_FAILED"})."),
                },
                _ => new SpeechProviderException(SpeechFailure.Unavailable, $"Khaya returned HTTP {(int)status}."),
            };
        }
        throw new SpeechProviderException(SpeechFailure.NotAuthorized, "Khaya rejected every configured subscription key.");
    }

    /// <summary>First error.details[].code, e.g. INVALID_AUDIO_FORMAT, UNSUPPORTED_LANGUAGE.</summary>
    private static async Task<string?> DetailCodeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("error", out var error)) return null;
            if (error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array && details.GetArrayLength() > 0
                && details[0].TryGetProperty("code", out var code))
                return code.GetString();
            return error.TryGetProperty("code", out var top) ? top.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
