using KasaSika.Domain;
using Microsoft.Extensions.Logging;

namespace KasaSika.Application.Speech;

/// <summary>
/// How the app should voice a reply.
/// "server": fetch <see cref="AudioUrl"/> (e.g. Khaya Twi voice). "device": speak <see cref="Text"/> with the
/// phone's own voice in <see cref="DeviceLocale"/>.
/// If server audio cannot be fetched, the app speaks <see cref="FallbackText"/> on the device instead, which
/// says honestly that the Twi voice is unavailable - it never reads Twi text with an English voice.
/// </summary>
public sealed record SpeechOutput(string Text, string Language, string Engine, string? AudioUrl, string? DeviceLocale, string? FallbackText);

/// <summary>Coordinates the speech providers. The only place that decides server vs. device voice.</summary>
public sealed class SpeechService(
    ILanguageRegistry languages,
    ISpeechRecognitionProvider recognizer,
    ITextToSpeechProvider tts,
    ISpeechClipStore clips,
    ILogger<SpeechService> logger)
{
    public const string ClipRoute = "/api/v1/speech/clips/";

    public bool CanRecognizeOnServer(LanguageInfo language) => recognizer.IsConfigured && language.ServerAsr;

    /// <param name="englishFallback">What to say on the device if the server voice cannot be played.</param>
    public SpeechOutput Prepare(string text, LanguageInfo language, string? englishFallback)
    {
        if (language.ServerTts && tts.IsConfigured)
        {
            var id = clips.Register(new SpeechClipRequest(text, language.Code, null));
            return new SpeechOutput(text, language.Code, "server", ClipRoute + id, languages.Default.DeviceLocale, englishFallback);
        }
        if (language.DeviceLocale is not null)
            return new SpeechOutput(text, language.Code, "device", null, language.DeviceLocale, null);
        // No voice for this language anywhere: speak the English fallback on the device.
        return new SpeechOutput(text, language.Code, "device", null, languages.Default.DeviceLocale, englishFallback);
    }

    /// <summary>Audio for a registered clip: from the cache, or synthesized once and cached.</summary>
    public async Task<AudioClip> GetClipAudioAsync(string clipId, CancellationToken ct)
    {
        var cached = clips.TryGetAudio(clipId);
        if (cached is not null) return cached;

        var request = clips.Find(clipId)
            ?? throw KasaException.NotFound("SPEECH_CLIP_NOT_FOUND", "That audio is not available.");
        var language = languages.Get(request.LanguageCode);
        try
        {
            var audio = await tts.SynthesizeAsync(request.Text, language, request.Voice, ct);
            clips.SaveAudio(clipId, audio);
            return audio;
        }
        catch (SpeechProviderException ex)
        {
            logger.LogWarning(ex, "Text-to-speech failed for {Language} ({Failure})", language.Code, ex.Failure);
            throw Unavailable(ex);
        }
    }

    public async Task<TranscriptionResult> TranscribeAsync(AudioClip audio, LanguageInfo language, CancellationToken ct)
    {
        if (audio.Data.Length == 0)
            throw KasaException.Validation("EMPTY_AUDIO", "No audio was received.");
        if (!recognizer.IsConfigured)
            throw new KasaException(ErrorKind.ServiceUnavailable, ErrorCodes.SpeechUnavailable, "Speech recognition is not configured on this server.");
        try
        {
            return await recognizer.TranscribeAsync(audio, language, ct);
        }
        catch (SpeechProviderException ex) when (ex.Failure == SpeechFailure.InvalidAudio)
        {
            throw KasaException.Validation("INVALID_AUDIO", "The recording could not be read.");
        }
        catch (SpeechProviderException ex)
        {
            logger.LogWarning(ex, "Speech recognition failed for {Language} ({Failure})", language.Code, ex.Failure);
            throw Unavailable(ex);
        }
    }

    private static KasaException Unavailable(SpeechProviderException ex) =>
        new(ErrorKind.ServiceUnavailable, ErrorCodes.SpeechUnavailable,
            ex.Failure == SpeechFailure.RateLimited ? "The voice service is busy. Please try again in a minute." : "The voice service is not available right now.",
            retryable: ex.Failure is not (SpeechFailure.NotAuthorized or SpeechFailure.UnsupportedLanguage));
}
