using KasaSika.Api.Infrastructure;
using KasaSika.Application.Abstractions;
using KasaSika.Application.Transactions;
using KasaSika.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KasaSika.Api.Controllers;

public sealed record StepUpRequest(Guid ChallengeId, AuthenticationMethod Method, string? Passcode, string? Signature);

/// <summary>
/// The transfer state machine, step by step. The voice app uses the conversation endpoints, which call the
/// same service; these granular endpoints exist for tests, tooling and non-voice clients. The user id always
/// comes from the token, never from the request.
/// </summary>
[ApiController, Authorize]
[Route("api/v1/transactions")]
public sealed class TransactionsController(TransactionService transactions, TimeProvider clock) : ControllerBase
{
    /// <summary>Accessible history: newest first, with plain-language status.</summary>
    [HttpGet]
    public async Task<IEnumerable<TransactionView>> List([FromQuery] int take = 20, [FromQuery] string language = "en", CancellationToken ct = default) =>
        (await transactions.ListAsync(User.UserId(), take, ct)).Select(t => TransactionMapper.ToView(t, language, clock));

    [HttpGet("{id:guid}")]
    public async Task<TransactionView> Get(Guid id, [FromQuery] string language = "en", CancellationToken ct = default) =>
        View(await transactions.GetAsync(User.UserId(), id, HttpContext.CorrelationId(), ct), language);

    [HttpPost]
    public async Task<TransactionView> Create([FromHeader(Name = "Idempotency-Key")] string idempotencyKey, DraftRequest request, CancellationToken ct) =>
        View(await transactions.CreateDraftAsync(User.UserId(), idempotencyKey, request, HttpContext.CorrelationId(), ct), request.Language ?? "en");

    [HttpPost("{id:guid}/confirm")]
    public async Task<TransactionView> Confirm(Guid id, CancellationToken ct) =>
        View(await transactions.ConfirmAsync(User.UserId(), id, HttpContext.CorrelationId(), ct));

    [HttpPost("{id:guid}/challenge")]
    public Task<ChallengeView> Challenge(Guid id, CancellationToken ct) =>
        transactions.IssueChallengeAsync(User.UserId(), id, User.DeviceId(), HttpContext.CorrelationId(), ct);

    [HttpPost("{id:guid}/authenticate")]
    public async Task<TransactionView> Authenticate(Guid id, StepUpRequest request, CancellationToken ct) =>
        View(await transactions.AuthenticateAsync(User.UserId(), id,
            new StepUpProof(request.ChallengeId, request.Method, request.Passcode, User.DeviceId(), request.Signature), HttpContext.CorrelationId(), ct));

    [HttpPost("{id:guid}/authorize")]
    public async Task<TransactionView> Authorize(Guid id, CancellationToken ct) =>
        View(await transactions.AuthorizeAsync(User.UserId(), id, HttpContext.CorrelationId(), ct));

    [HttpPost("{id:guid}/submit")]
    public async Task<TransactionView> Submit(Guid id, CancellationToken ct) =>
        View(await transactions.SubmitAsync(User.UserId(), id, HttpContext.CorrelationId(), ct));

    [HttpPost("{id:guid}/status")]
    public async Task<TransactionView> Status(Guid id, CancellationToken ct) =>
        View(await transactions.RefreshStatusAsync(User.UserId(), id, HttpContext.CorrelationId(), ct));

    [HttpPost("{id:guid}/cancel")]
    public async Task<TransactionView> Cancel(Guid id, CancellationToken ct) =>
        View(await transactions.CancelAsync(User.UserId(), id, HttpContext.CorrelationId(), ct));

    private TransactionView View(Domain.Transactions.Transaction tx, string? language = null) =>
        TransactionMapper.ToView(tx, language ?? tx.Language, clock);
}
