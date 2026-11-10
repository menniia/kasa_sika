using KasaSika.Api.Infrastructure;
using KasaSika.Application.Abstractions;
using KasaSika.Application.Conversations;
using KasaSika.Application.Demo;
using KasaSika.Application.Language;
using KasaSika.Application.Options;
using KasaSika.Application.Speech;
using KasaSika.Application.Transactions;
using KasaSika.Domain.Transactions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace KasaSika.Api.Controllers;

public sealed record LanguageView(string Code, string Name, string NativeName, string? DeviceLocale, bool ServerTts, bool ServerAsr);

/// <param name="EnvironmentLabel">Always shown in the app: "DEMO — MOCK PAYMENTS" or "MTN SANDBOX". Never "LIVE".</param>
public sealed record SystemInfo(
    string EnvironmentLabel, ProviderMode Mode, string Provider, ProviderCapabilities Capabilities,
    IReadOnlyList<LanguageView> Languages, bool KhayaConfigured, bool DemoEnabled, bool DemoLogin,
    IReadOnlyList<string> StepUpMethods, long MaxTransactionMinor);

[ApiController]
[Route("api/v1/system")]
public sealed class SystemController(
    IPaymentProvider provider, ILanguageRegistry languages, ISpeechRecognitionProvider asr,
    IKasaDbContext db, TransactionService transactions,
    IOptions<DemoOptions> demo, IOptions<LimitsOptions> limits) : ControllerBase
{
    [HttpGet("info"), AllowAnonymous]
    public SystemInfo Info() => new(
        provider.Mode switch { ProviderMode.Sandbox => "MTN SANDBOX — TEST MONEY", ProviderMode.DemoSimulated => "DEMO — MOCK PAYMENTS", _ => "UNSUPPORTED" },
        provider.Mode,
        provider.Name,
        provider.Capabilities,
        languages.All.Where(l => l.Enabled).Select(l => new LanguageView(l.Code, l.EnglishName, l.NativeName, l.DeviceLocale, l.ServerTts, l.ServerAsr)).ToList(),
        asr.IsConfigured,
        demo.Value.Enabled,
        demo.Value.Enabled && demo.Value.AllowPasswordlessDemoLogin,
        transactions.EnabledMethods().Select(m => m.ToString()).ToList(),
        limits.Value.MaxTransactionMinor);

    [HttpGet("health"), AllowAnonymous]
    public async Task<IActionResult> Health(CancellationToken ct) =>
        await db.CanConnectAsync(ct) ? Ok(new { status = "ok", database = "ok" }) : StatusCode(503, new { status = "degraded", database = "unreachable" });
}

/// <summary>Synthesized replies (Khaya Twi voice), served from the content-addressed cache.</summary>
[ApiController]
[Route("api/v1/speech")]
public sealed class SpeechController(SpeechService speech) : ControllerBase
{
    /// <summary>
    /// Anonymous on purpose (audio players cannot send auth headers). Ids are 160-bit hashes of registered
    /// replies only; an unregistered id is 404 and never triggers synthesis, so this cannot spend the quota.
    /// </summary>
    [HttpGet("clips/{clipId}"), AllowAnonymous, EnableRateLimiting("clips")]
    public async Task<IActionResult> Clip(string clipId, CancellationToken ct)
    {
        var audio = await speech.GetClipAudioAsync(clipId, ct);
        Response.Headers.CacheControl = "private, max-age=86400";
        return File(audio.Data, audio.ContentType);
    }
}

/// <summary>Provider callbacks. Untrusted hints only: the result is always re-read from the provider.</summary>
[ApiController]
[Route("api/v1/callbacks")]
public sealed class CallbacksController(TransactionService transactions) : ControllerBase
{
    [HttpPost("{provider}"), HttpPut("{provider}"), AllowAnonymous, EnableRateLimiting("callbacks")]
    [RequestSizeLimit(16 * 1024)]
    public async Task<IActionResult> Receive(string provider, CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(ct);
        var headers = Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var result = await transactions.HandleCallbackAsync(provider, headers, body, HttpContext.CorrelationId(), ct);
        return Ok(new { result = result.ToString() });
    }
}

public sealed record ScenarioRequest(string? Scenario);

/// <summary>Presenter controls. Only when Demo:Enabled, only for demo accounts, never with a live provider.</summary>
[ApiController, Authorize]
[Route("api/v1/demo")]
public sealed class DemoController(DemoService demo, SpeechService speech, ILanguageRegistry languages, IOptions<DemoOptions> options, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    public DemoStateView State() => demo.State();

    [HttpPut("scenario")]
    public DemoStateView SetScenario(ScenarioRequest request)
    {
        demo.SetScenario(request.Scenario);
        return demo.State();
    }

    [HttpPost("reset")]
    public async Task<IActionResult> Reset(CancellationToken ct)
    {
        var cleared = await demo.ResetAsync(User.UserId(), HttpContext.CorrelationId(), ct);
        return Ok(new { cleared });
    }

    /// <summary>
    /// Pre-generates the Twi audio for the scripted demo so the live run is instant and does not depend on the
    /// network. Spends one Khaya call per sentence not yet cached. Run once after changing Twi templates.
    /// </summary>
    [HttpPost("prewarm-speech")]
    public async Task<IActionResult> Prewarm([FromQuery] string language = "tw", CancellationToken ct = default)
    {
        if (!options.Value.Enabled) throw Domain.KasaException.Forbidden("DEMO_DISABLED", "Demo controls are not enabled.");
        var lang = languages.Get(language);
        if (!lang.ServerTts) return Ok(new { generated = 0, note = "This language is spoken by the phone's own voice." });

        var ama = new ReplyArgs { AmountMinor = 5000, Name = "Ama Owusu", LastFour = "0111" };
        List<List<ReplyPart>> script =
        [
            [ReplyPart.Of("MODE_MOCK"), ReplyPart.Of("WELCOME", new ReplyArgs { Name = options.Value.UserName })],
            [ReplyPart.Of("CONFIRM_SEND", ama)],
            [ReplyPart.Of("AUTH_REQUIRED")],
            [ReplyPart.Of("PROCESSING"), ReplyPart.Of("PENDING")],
            [ReplyPart.Of("SUCCESS", ama)],
            [ReplyPart.Of("CANCELLED")],
            [ReplyPart.Of("NOT_UNDERSTOOD")],
            [ReplyPart.Of("CONFIRM_EXPECTED")],
            [ReplyPart.Of("HELP_IDLE")],
        ];
        var results = new List<object>();
        foreach (var parts in script)
        {
            var output = speech.Prepare(ResponseCatalog.Render(parts, lang.Code, clock), lang, null);
            if (output.AudioUrl is null) continue;
            var id = output.AudioUrl[SpeechService.ClipRoute.Length..];
            try
            {
                var audio = await speech.GetClipAudioAsync(id, ct);
                results.Add(new { key = parts[^1].Key, bytes = audio.Data.Length });
            }
            catch (Domain.KasaException ex)
            {
                results.Add(new { key = parts[^1].Key, error = ex.Code });
            }
        }
        return Ok(new { generated = results.Count, results });
    }
}
