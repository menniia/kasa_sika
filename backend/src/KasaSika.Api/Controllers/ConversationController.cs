using KasaSika.Api.Infrastructure;
using KasaSika.Application.Conversations;
using KasaSika.Application.Speech;
using KasaSika.Domain;
using KasaSika.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KasaSika.Api.Controllers;

/// <param name="AudioBase64">A recording (WAV/MP3/FLAC/OGG) for Khaya recognition, base64-encoded. Either Text or AudioBase64.</param>
public sealed record TurnRequest(
    Guid? ConversationId, string TurnId, string? Language, string? Text, string? InputMode, string? RecognizedBy,
    string? AudioBase64 = null, string? AudioContentType = null);

public sealed record AuthenticateRequest(Guid ChallengeId, AuthenticationMethod Method, string? Passcode, string? Signature);

/// <summary>
/// The voice conversation. The app sends what the user said (text from the phone's recogniser, or a raw
/// recording for Khaya), and renders + speaks what comes back.
/// </summary>
[ApiController, Authorize]
[Route("api/v1/conversation")]
public sealed class ConversationController(ConversationService conversations) : ControllerBase
{
    private const long MaxAudioBytes = 5 * 1024 * 1024; // ~2.5 minutes of 16 kHz WAV: far more than one request needs

    /// <summary>Open the app: greeting, results that arrived while away, and the current conversation.</summary>
    [HttpGet]
    public Task<ConversationReply> Welcome([FromQuery] string? language, CancellationToken ct) =>
        conversations.WelcomeAsync(User.UserId(), language, HttpContext.CorrelationId(), ct);

    /// <summary>A typed or device-recognised utterance, or a recording (base64) for Khaya.</summary>
    [HttpPost("turns")]
    [RequestSizeLimit(MaxAudioBytes * 4 / 3 + 64 * 1024)]
    public Task<ConversationReply> Turn(TurnRequest request, CancellationToken ct)
    {
        AudioClip? audio = null;
        if (!string.IsNullOrEmpty(request.AudioBase64))
        {
            byte[] bytes;
            try { bytes = Convert.FromBase64String(request.AudioBase64); }
            catch (FormatException) { throw KasaException.Validation("INVALID_AUDIO", "The recording could not be read."); }
            if (bytes.Length > MaxAudioBytes) throw KasaException.Validation("AUDIO_TOO_LONG", "The recording is too long.");
            audio = new AudioClip(bytes, request.AudioContentType ?? "audio/wav");
        }
        return conversations.HandleTurnAsync(User.UserId(), User.DeviceId(),
            new TurnInput(request.ConversationId, request.TurnId, request.Language, audio is null ? request.Text : null, audio,
                request.InputMode ?? (audio is null ? "text" : "voice"), request.RecognizedBy),
            HttpContext.CorrelationId(), ct);
    }

    /// <summary>A recorded utterance (WAV/MP3/FLAC/OGG) for server-side recognition by Khaya.</summary>
    [HttpPost("turns/audio")]
    [RequestSizeLimit(MaxAudioBytes + 64 * 1024)]
    public async Task<ConversationReply> AudioTurn([FromForm] string turnId, [FromForm] Guid? conversationId, [FromForm] string? language, IFormFile audio, CancellationToken ct)
    {
        if (audio is null || audio.Length == 0) throw KasaException.Validation("EMPTY_AUDIO", "No audio was received.");
        if (audio.Length > MaxAudioBytes) throw KasaException.Validation("AUDIO_TOO_LONG", "The recording is too long.");
        using var buffer = new MemoryStream();
        await audio.CopyToAsync(buffer, ct);
        var contentType = string.IsNullOrWhiteSpace(audio.ContentType) || audio.ContentType == "application/octet-stream"
            ? GuessType(audio.FileName) : audio.ContentType;
        return await conversations.HandleTurnAsync(User.UserId(), User.DeviceId(),
            new TurnInput(conversationId, turnId, language, null, new AudioClip(buffer.ToArray(), contentType), "voice", null),
            HttpContext.CorrelationId(), ct);
    }

    /// <summary>Answer the step-up challenge (passcode or device biometric). On success the transfer is sent.</summary>
    [HttpPost("{conversationId:guid}/authenticate")]
    public Task<ConversationReply> Authenticate(Guid conversationId, AuthenticateRequest request, CancellationToken ct) =>
        conversations.AuthenticateAsync(User.UserId(),
            new AuthenticateInput(conversationId, request.ChallengeId, request.Method, request.Passcode, User.DeviceId(), request.Signature),
            HttpContext.CorrelationId(), ct);

    [HttpPost("{conversationId:guid}/status")]
    public Task<ConversationReply> Status(Guid conversationId, CancellationToken ct) =>
        conversations.CheckStatusAsync(User.UserId(), conversationId, HttpContext.CorrelationId(), ct);

    [HttpPost("{conversationId:guid}/reset")]
    public Task<ConversationReply> Reset(Guid conversationId, CancellationToken ct) =>
        conversations.ResetAsync(User.UserId(), conversationId, HttpContext.CorrelationId(), ct);

    private static string GuessType(string? fileName) => Path.GetExtension(fileName ?? "").ToLowerInvariant() switch
    {
        ".wav" => "audio/wav",
        ".mp3" => "audio/mpeg",
        ".flac" => "audio/flac",
        ".ogg" or ".opus" => "audio/ogg",
        _ => "application/octet-stream",
    };
}
