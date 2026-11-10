using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KasaSika.Application.Options;
using KasaSika.Application.Speech;
using KasaSika.Domain;
using Microsoft.Extensions.Options;

namespace KasaSika.Infrastructure.Speech;

/// <summary>Languages from configuration ("Languages" section). Disabled languages are known but refused.</summary>
public sealed class ConfiguredLanguageRegistry : ILanguageRegistry
{
    private readonly Dictionary<string, LanguageInfo> _byCode;

    public ConfiguredLanguageRegistry(IOptions<LanguageOptions> options)
    {
        var configured = options.Value.Supported.Count > 0 ? options.Value.Supported : DefaultLanguages();
        _byCode = configured.ToDictionary(
            l => l.Code,
            l => new LanguageInfo(l.Code, l.EnglishName, l.NativeName, l.Enabled, l.ProviderCode, l.DeviceLocale, l.ServerTts, l.ServerAsr),
            StringComparer.OrdinalIgnoreCase);
        All = _byCode.Values.ToList();
        Default = Find(options.Value.Default) ?? All.First(l => l.Enabled);
    }

    public IReadOnlyList<LanguageInfo> All { get; }
    public LanguageInfo Default { get; }

    public LanguageInfo? Find(string? code) =>
        code is not null && _byCode.TryGetValue(code, out var l) && l.Enabled ? l : null;

    public LanguageInfo Get(string? code) =>
        code is null ? Default
        : Find(code) ?? throw KasaException.Validation("UNSUPPORTED_LANGUAGE", "That language is not available yet.");

    private static List<LanguageConfig> DefaultLanguages() =>
    [
        new() { Code = "en", EnglishName = "English", NativeName = "English", Enabled = true, ProviderCode = "eng", DeviceLocale = "en-GH", ServerTts = false, ServerAsr = true },
        new() { Code = "tw", EnglishName = "Twi", NativeName = "Twi", Enabled = true, ProviderCode = "twi", DeviceLocale = null, ServerTts = true, ServerAsr = true },
    ];
}

/// <summary>
/// Content-addressed disk cache of synthesized replies. The same sentence in the same language and voice is
/// synthesized once, ever: important on Khaya's free tier (100 calls/month), and it makes a rehearsed demo
/// instant and independent of network hiccups.
/// </summary>
public sealed class FileSpeechClipStore : ISpeechClipStore
{
    private readonly string _directory;
    private readonly ConcurrentDictionary<string, SpeechClipRequest> _requests = new();

    public FileSpeechClipStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    public string Register(SpeechClipRequest request)
    {
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{request.LanguageCode}|{request.Voice}|{request.Text}")))[..40];
        if (_requests.TryAdd(id, request))
        {
            var path = Path.Combine(_directory, id + ".json");
            if (!File.Exists(path)) File.WriteAllText(path, JsonSerializer.Serialize(request));
        }
        return id;
    }

    public SpeechClipRequest? Find(string clipId)
    {
        if (!IsValidId(clipId)) return null;
        if (_requests.TryGetValue(clipId, out var request)) return request;
        var path = Path.Combine(_directory, clipId + ".json");
        return File.Exists(path) ? JsonSerializer.Deserialize<SpeechClipRequest>(File.ReadAllText(path)) : null;
    }

    public AudioClip? TryGetAudio(string clipId)
    {
        if (!IsValidId(clipId)) return null;
        var audio = Path.Combine(_directory, clipId + ".audio");
        var type = Path.Combine(_directory, clipId + ".type");
        return File.Exists(audio) && File.Exists(type) ? new AudioClip(File.ReadAllBytes(audio), File.ReadAllText(type)) : null;
    }

    public void SaveAudio(string clipId, AudioClip audio)
    {
        if (!IsValidId(clipId)) return;
        File.WriteAllBytes(Path.Combine(_directory, clipId + ".audio"), audio.Data);
        File.WriteAllText(Path.Combine(_directory, clipId + ".type"), audio.ContentType);
    }

    /// <summary>Ids are hex hashes; anything else (e.g. "../") never touches the file system.</summary>
    private static bool IsValidId(string id) => id.Length == 40 && id.All(Uri.IsHexDigit);
}
