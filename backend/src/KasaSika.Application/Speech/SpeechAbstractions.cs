namespace KasaSika.Application.Speech;

/// <summary>
/// A language Kasa Sika can converse in. <see cref="Code"/> is the app's own code ("en", "tw"); the
/// provider codes are whatever each provider expects (Khaya uses ISO 639-3: "eng", "twi"). Adding Ewe, Ga or
/// Dagbani is configuration plus a response catalogue, not new code paths.
/// </summary>
/// <param name="DeviceLocale">BCP-47 locale for on-device speech (e.g. "en-GH"), or null when the phone has no voice for it.</param>
/// <param name="ServerTts">Speak replies with the server's TTS provider (Twi: Khaya), rather than the phone's own voice.</param>
/// <param name="ServerAsr">Recognise speech on the server (Khaya), rather than the phone's recogniser.</param>
public sealed record LanguageInfo(
    string Code, string EnglishName, string NativeName, bool Enabled,
    string ProviderCode, string? DeviceLocale, bool ServerTts, bool ServerAsr);

public interface ILanguageRegistry
{
    IReadOnlyList<LanguageInfo> All { get; }
    LanguageInfo Default { get; }
    /// <summary>Returns the enabled language for <paramref name="code"/>, or throws UNSUPPORTED_LANGUAGE.</summary>
    LanguageInfo Get(string? code);
    LanguageInfo? Find(string? code);
}

public sealed record AudioClip(byte[] Data, string ContentType);

public sealed record TranscriptionResult(string Text, string RecognizedBy);

public enum SpeechFailure
{
    /// <summary>Provider down, 5xx, network error.</summary>
    Unavailable,
    Timeout,
    RateLimited,
    /// <summary>Our subscription key was rejected (401/403) or the quota is exhausted.</summary>
    NotAuthorized,
    InvalidAudio,
    UnsupportedLanguage,
    InvalidInput,
}

/// <summary>A speech/language provider could not do what was asked. Never swallowed into a fabricated result.</summary>
public sealed class SpeechProviderException(SpeechFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    public SpeechFailure Failure { get; } = failure;
}

/// <summary>Speech to text. Implementations: KhayaSpeechRecognitionProvider.</summary>
public interface ISpeechRecognitionProvider
{
    string Name { get; }
    bool IsConfigured { get; }
    Task<TranscriptionResult> TranscribeAsync(AudioClip audio, LanguageInfo language, CancellationToken cancellationToken);
}

/// <summary>Text translation. Used only for text that has no curated template; see ResponseCatalog.</summary>
public interface ITranslationProvider
{
    string Name { get; }
    bool IsConfigured { get; }
    Task<string> TranslateAsync(string text, LanguageInfo from, LanguageInfo to, CancellationToken cancellationToken);
}

/// <summary>Text to speech. Implementations: KhayaTextToSpeechProvider, wrapped in a disk cache.</summary>
public interface ITextToSpeechProvider
{
    string Name { get; }
    bool IsConfigured { get; }
    Task<AudioClip> SynthesizeAsync(string text, LanguageInfo language, string? voice, CancellationToken cancellationToken);
}

/// <summary>A reply registered for server-side speech: the app fetches its audio by id.</summary>
public sealed record SpeechClipRequest(string Text, string LanguageCode, string? Voice);

/// <summary>
/// Content-addressed store for synthesized replies. The clip id is a hash of language, voice and text, so
/// the same sentence is synthesized once and then served from disk (Khaya's free tier is 100 calls/month).
/// </summary>
public interface ISpeechClipStore
{
    string Register(SpeechClipRequest request);
    SpeechClipRequest? Find(string clipId);
    AudioClip? TryGetAudio(string clipId);
    void SaveAudio(string clipId, AudioClip audio);
}
