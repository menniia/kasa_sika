using KasaSika.Application.Speech;
using Microsoft.Extensions.Options;

namespace KasaSika.Infrastructure.Speech.Khaya;

/// <summary>Khaya ASR v3: Ghanaian-language (and African English) speech to text.</summary>
public sealed class KhayaSpeechRecognitionProvider(KhayaClient client, IOptions<KhayaOptions> options) : ISpeechRecognitionProvider
{
    private static readonly HashSet<string> Accepted = ["audio/wav", "audio/x-wav", "audio/wave", "audio/mpeg", "audio/mp3", "audio/flac", "audio/ogg"];

    public string Name => "khaya";
    public bool IsConfigured => options.Value.IsConfigured;

    public async Task<TranscriptionResult> TranscribeAsync(AudioClip audio, LanguageInfo language, CancellationToken cancellationToken)
    {
        var type = audio.ContentType.Split(';')[0].Trim().ToLowerInvariant();
        if (!Accepted.Contains(type))
            throw new SpeechProviderException(SpeechFailure.InvalidAudio, $"Khaya accepts WAV, MP3, FLAC or OGG audio, not {type}.");
        var normalizedType = type switch { "audio/x-wav" or "audio/wave" => "audio/wav", "audio/mp3" => "audio/mpeg", _ => type };
        var text = await client.TranscribeAsync(audio.Data, normalizedType, language.ProviderCode, cancellationToken);
        return new TranscriptionResult(text, $"khaya:{language.ProviderCode}");
    }
}

/// <summary>Khaya Translation v2. Used only as a fallback for text with no curated template.</summary>
public sealed class KhayaTranslationProvider(KhayaClient client, IOptions<KhayaOptions> options) : ITranslationProvider
{
    public string Name => "khaya";
    public bool IsConfigured => options.Value.IsConfigured;

    public Task<string> TranslateAsync(string text, LanguageInfo from, LanguageInfo to, CancellationToken cancellationToken) =>
        client.TranslateAsync(text, from.ProviderCode, to.ProviderCode, cancellationToken);
}

/// <summary>Khaya TTS v2: the Twi voice of Kasa Sika.</summary>
public sealed class KhayaTextToSpeechProvider(KhayaClient client, IOptions<KhayaOptions> options) : ITextToSpeechProvider
{
    public string Name => "khaya";
    public bool IsConfigured => options.Value.IsConfigured;

    public async Task<AudioClip> SynthesizeAsync(string text, LanguageInfo language, string? voice, CancellationToken cancellationToken)
    {
        var (audio, type) = await client.SynthesizeAsync(text, language.ProviderCode, voice, cancellationToken);
        return new AudioClip(audio, type);
    }
}
