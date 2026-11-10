using System.Security.Cryptography;
using System.Text.RegularExpressions;
using KasaSika.Application.Abstractions;
using KasaSika.Application.Options;
using KasaSika.Domain;
using KasaSika.Domain.Identity;
using KasaSika.Domain.Payments;
using KasaSika.Domain.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KasaSika.Application.Transactions;

public sealed record DraftRequest(long AmountMinor, string Currency, string RecipientName, string RecipientPhoneNumber, string? Language);

public sealed record CreateTransfer(
    Guid UserId, string IdempotencyKey, Money Amount, string RecipientName, Msisdn Recipient,
    Guid? ContactId, string Language, Guid? ConversationId);

/// <param name="SigningMessage">What a phone must sign (HMAC with its enrolled device key) for a biometric proof.</param>
public sealed record ChallengeView(
    Guid ChallengeId, Guid TransactionId, DateTimeOffset ExpiresAt,
    IReadOnlyList<AuthenticationMethod> Methods, string SigningMessage);

public enum CallbackResult { Applied, Duplicate, Ignored }

/// <summary>
/// Orchestrates a transfer. Kasa Sika does not move money: it authenticates the user, records intent and
/// state, asks MTN to move the money, and then follows up until MTN gives an answer.
///
/// Every method takes the caller's user id (from the JWT, never from the request body) and can only ever
/// see that user's transactions: someone else's id is indistinguishable from one that does not exist.
/// </summary>
public sealed partial class TransactionService(
    IKasaDbContext db,
    IPaymentProvider provider,
    IEnumerable<IStepUpAuthenticator> authenticators,
    IFieldProtector protector,
    ProviderStatusSync statusSync,
    TransactionOutcomes outcomes,
    TimeProvider clock,
    IOptions<LimitsOptions> limits,
    IOptions<AuthOptions> authOptions,
    ILogger<TransactionService> logger)
{
    public const string ConsentScope = "SEND_MONEY_TRANSFER";

    [GeneratedRegex(@"^[A-Za-z0-9._:-]{8,128}$")]
    private static partial Regex IdempotencyKeyPattern();

    // ------------------------------------------------------------------------------------------ create

    public async Task<Transaction> CreateDraftAsync(Guid userId, string idempotencyKey, DraftRequest request, string correlationId, CancellationToken ct)
    {
        var name = (request.RecipientName ?? "").Trim();
        if (name.Length is < 1 or > 80)
            throw KasaException.Validation("INVALID_RECIPIENT_NAME", "Please give the recipient's name (up to 80 characters).");
        Money amount;
        try
        {
            amount = Money.FromMinor(request.AmountMinor, request.Currency);
        }
        catch (KasaException ex) when (ex.Kind == ErrorKind.Validation)
        {
            throw KasaException.Validation(ErrorCodes.InvalidAmount, ex.Message);
        }
        return await CreateAsync(new CreateTransfer(userId, idempotencyKey, amount, name,
            Msisdn.Parse(request.RecipientPhoneNumber), null, request.Language ?? "en", null), correlationId, ct);
    }

    /// <summary>
    /// Creates the transfer and validates the recipient with the provider. Idempotent per (user, key): the same
    /// request returns the same transaction; the same key for a different request is refused.
    /// </summary>
    public async Task<Transaction> CreateAsync(CreateTransfer command, string correlationId, CancellationToken ct)
    {
        if (command.IdempotencyKey is null || !IdempotencyKeyPattern().IsMatch(command.IdempotencyKey))
            throw KasaException.Validation("IDEMPOTENCY_KEY_REQUIRED", "An Idempotency-Key of 8 to 128 letters, digits or - . _ : is required.");
        if (command.Amount.Currency != "GHS")
            throw KasaException.Validation("UNSUPPORTED_CURRENCY", "Only GHS transfers are supported.");
        if (command.Amount.AmountMinor > limits.Value.MaxTransactionMinor)
        {
            var ex = KasaException.Validation("AMOUNT_ABOVE_LIMIT",
                $"The amount is above the per-transfer limit of {Money.FromMinor(limits.Value.MaxTransactionMinor, "GHS").ToSpeech()}.");
            ex.Data["limitMinor"] = limits.Value.MaxTransactionMinor;
            throw ex;
        }

        // Keyed hash, not plain SHA-256: a plain hash of a phone number could be reversed by brute force.
        var fingerprint = protector.Fingerprint($"create|{command.UserId:N}|{command.Amount.AmountMinor}|{command.Amount.Currency}|{command.Recipient.International}");

        var existing = await FindByKeyAsync(command.UserId, command.IdempotencyKey, ct);
        if (existing is not null) return await ResumeAsync(existing, fingerprint, correlationId, ct);

        var now = clock.GetUtcNow();
        await EnforceDailyLimitAsync(command.UserId, command.Amount, now, ct);

        var tx = Transaction.Create(command.UserId, command.IdempotencyKey, fingerprint, command.Amount, command.RecipientName,
            command.Recipient, command.ContactId, command.Language, command.ConversationId,
            provider.Name, provider.Mode, now, TimeSpan.FromMinutes(limits.Value.TransactionTtlMinutes), correlationId);
        db.Transactions.Add(tx);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two identical requests raced; the unique index on (user, idempotency key) let only one in.
            db.ChangeTracker.Clear();
            var winner = await FindByKeyAsync(command.UserId, command.IdempotencyKey, ct) ?? throw new InvalidOperationException("Insert failed and no existing row was found.");
            return await ResumeAsync(winner, fingerprint, correlationId, ct);
        }
        return await ValidateRecipientAsync(tx, correlationId, ct);
    }

    /// <summary>The client retried a create with the same Idempotency-Key. Same request -> same transaction; different request -> error.</summary>
    private async Task<Transaction> ResumeAsync(Transaction existing, string fingerprint, string correlationId, CancellationToken ct)
    {
        if (!string.Equals(existing.RequestFingerprint, fingerprint, StringComparison.Ordinal))
            throw KasaException.Conflict(ErrorCodes.DuplicateTransaction, "That Idempotency-Key was already used for a different request.");
        if (existing.State != TransactionState.Parsed) return existing;

        // A request the provider was unavailable for is retried, so a retry after an outage completes the request.
        try
        {
            return await ValidateRecipientAsync(existing, correlationId, ct);
        }
        catch (KasaException ex) when (ex.Code == "CONCURRENT_UPDATE")
        {
            // Another identical request is validating the same draft right now. Report whatever it produced.
            db.ChangeTracker.Clear();
            var current = await FindByKeyAsync(existing.UserId, existing.IdempotencyKey, ct);
            if (current is null) throw;
            return current;
        }
    }

    private async Task<Transaction> ValidateRecipientAsync(Transaction tx, string correlationId, CancellationToken ct)
    {
        tx.BeginValidation(clock.GetUtcNow(), correlationId);
        await db.SaveOrConflictAsync(ct);

        bool active;
        try
        {
            active = await provider.IsAccountHolderActiveAsync(Msisdn.FromStored(tx.RecipientMsisdn), ct);
        }
        catch (ProviderUnavailableException ex)
        {
            logger.LogWarning(ex, "Recipient validation for {TransactionId} failed: provider unavailable", tx.Id);
            tx.ValidationInterrupted(clock.GetUtcNow(), correlationId);
            await db.SaveOrConflictAsync(CancellationToken.None);
            throw new KasaException(ErrorKind.ProviderFailure, ErrorCodes.ProviderUnavailable,
                "The payment provider is not available right now. Please try again in a moment.", retryable: true);
        }

        var now = clock.GetUtcNow();
        if (!active)
        {
            tx.FailValidation("RECIPIENT_NOT_ACTIVE", now, correlationId);
            await db.SaveOrConflictAsync(ct);
            return tx;
        }
        tx.MarkReadyForConfirmation(now, correlationId);
        await db.SaveOrConflictAsync(ct);
        return tx;
    }

    /// <summary>Rolling 24 h cap on money sent or possibly in flight. Transfers that failed or never started do not count.</summary>
    private async Task EnforceDailyLimitAsync(Guid userId, Money amount, DateTimeOffset now, CancellationToken ct)
    {
        var since = now.AddHours(-24);
        TransactionState[] counted =
        [
            TransactionState.Submitting, TransactionState.Submitted, TransactionState.Pending, TransactionState.Success,
            TransactionState.Unknown, TransactionState.ReconciliationRequired,
        ];
        var used = await db.Transactions
            .Where(t => t.UserId == userId && t.CreatedAt >= since && counted.Contains(t.State))
            .SumAsync(t => t.AmountMinor, ct);
        if (used + amount.AmountMinor > limits.Value.MaxDailyMinor)
            throw KasaException.Validation("DAILY_LIMIT_EXCEEDED", "This would exceed your daily transfer limit.");
    }

    // ------------------------------------------------------------------------------------------ read

    public async Task<Transaction> GetAsync(Guid userId, Guid id, string correlationId, CancellationToken ct)
    {
        var tx = await LoadOwnedAsync(userId, id, ct);
        if (tx.ExpireIfDue(clock.GetUtcNow(), correlationId)) await db.SaveOrConflictAsync(ct);
        return tx;
    }

    public async Task<IReadOnlyList<Transaction>> ListAsync(Guid userId, int take, CancellationToken ct) =>
        await db.Transactions.AsNoTracking()
            .Include(t => t.Events)
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.CreatedAt)
            .Take(Math.Clamp(take, 1, 50))
            .ToListAsync(ct);

    // ------------------------------------------------------------------------------------------ steps

    public async Task<Transaction> ConfirmAsync(Guid userId, Guid id, string correlationId, CancellationToken ct)
    {
        var tx = await LoadForActionAsync(userId, id, correlationId, ct);
        if (tx.State is TransactionState.Confirmed or TransactionState.AuthenticationPending) return tx; // replay
        tx.Confirm(clock.GetUtcNow(), correlationId);
        await db.SaveOrConflictAsync(ct);
        return tx;
    }

    /// <summary>
    /// Issues a single-use challenge bound to this exact transfer. Calling again replaces nothing: each call
    /// gives a fresh challenge and the old ones simply expire unused.
    /// </summary>
    public async Task<ChallengeView> IssueChallengeAsync(Guid userId, Guid id, Guid? deviceId, string correlationId, CancellationToken ct)
    {
        var tx = await LoadForActionAsync(userId, id, correlationId, ct);
        if (tx.State is not (TransactionState.Confirmed or TransactionState.AuthenticationPending))
            throw KasaException.Conflict("INVALID_TRANSACTION_TRANSITION", $"A transaction in state {tx.State} does not need authentication.");

        var now = clock.GetUtcNow();
        var nonce = Abstractions.DeviceProof.Base64Url(RandomNumberGenerator.GetBytes(24));
        var challenge = AuthenticationChallenge.Issue(userId, tx.Id, deviceId, nonce, BindingFingerprint(tx), now,
            TimeSpan.FromSeconds(authOptions.Value.ChallengeSeconds));
        db.AuthenticationChallenges.Add(challenge);
        tx.BeginAuthentication(challenge.Id, now, correlationId);
        await db.SaveOrConflictAsync(ct);
        return new ChallengeView(challenge.Id, tx.Id, challenge.ExpiresAt, EnabledMethods(), Abstractions.DeviceProof.Message(challenge, tx));
    }

    public IReadOnlyList<AuthenticationMethod> EnabledMethods() =>
        authenticators.Select(a => a.Method).Where(m => authOptions.Value.EffectiveStepUpMethods.Contains(m)).Distinct().ToList();

    /// <summary>
    /// Step-up authentication for THIS transfer. The proof must answer an unexpired, unused challenge that was
    /// issued for this transaction with the same amount/recipient. On success the result is bound to the
    /// transfer via a fingerprint that is re-checked at submission.
    /// </summary>
    public async Task<Transaction> AuthenticateAsync(Guid userId, Guid id, StepUpProof proof, string correlationId, CancellationToken ct)
    {
        var tx = await LoadForActionAsync(userId, id, correlationId, ct);
        var now = clock.GetUtcNow();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw KasaException.Unauthenticated("UNAUTHENTICATED", "Please sign in again.");

        // Check the state first so a wrongly-ordered call does not burn one of the user's limited attempts.
        if (tx.State != TransactionState.AuthenticationPending)
            throw KasaException.Conflict("INVALID_TRANSACTION_TRANSITION", $"A transaction in state {tx.State} cannot be authenticated.");

        var challenge = await db.AuthenticationChallenges.FirstOrDefaultAsync(c => c.Id == proof.ChallengeId && c.UserId == userId && c.TransactionId == tx.Id, ct)
            ?? throw KasaException.NotFound("CHALLENGE_NOT_FOUND", "Please start the verification again.");
        if (!challenge.IsUsable(now))
            throw new KasaException(ErrorKind.Gone, "CHALLENGE_EXPIRED", "This verification expired or was already used. Please try again.");
        if (!string.Equals(challenge.BindingFingerprint, BindingFingerprint(tx), StringComparison.Ordinal))
            throw KasaException.Conflict("AUTHORIZATION_MISMATCH", "The transaction changed after it was confirmed. Please start again.");

        var authenticator = authenticators.FirstOrDefault(a => a.Method == proof.Method && authOptions.Value.EffectiveStepUpMethods.Contains(a.Method))
            ?? throw KasaException.Validation("AUTHENTICATION_METHOD_NOT_ALLOWED", "That verification method is not available.");

        AuthenticationAttempt Attempt(AuthenticationResult result, string reason) =>
            new AuthenticationAttempt(userId, tx.Id, proof.Method, result, reason, now)
                .ForTransfer(proof.DeviceId, challenge.Id, tx.AmountMinor, tx.Currency, tx.RecipientMasked);

        if (user.IsLocked(now))
        {
            db.AuthenticationAttempts.Add(Attempt(AuthenticationResult.Locked, "Attempt while locked"));
            await db.SaveChangesAsync(ct);
            throw new KasaException(ErrorKind.Locked, ErrorCodes.AuthenticationLocked, "Too many wrong attempts. Please try again later.");
        }
        if (proof.Method == AuthenticationMethod.Passcode && string.IsNullOrEmpty(proof.Passcode))
            throw KasaException.Validation("PASSCODE_REQUIRED", "Please enter your passcode.");

        if (!await authenticator.VerifyAsync(new StepUpContext(user, tx, challenge), proof, ct))
        {
            var max = authOptions.Value.MaxFailedAttempts;
            user.RecordFailedAttempt(now, max, TimeSpan.FromMinutes(authOptions.Value.LockoutMinutes));
            challenge.RecordFailure();
            db.AuthenticationAttempts.Add(Attempt(AuthenticationResult.Failure, "Wrong credential"));
            if (user.IsLocked(now))
                db.AuditEvents.Add(new AuditEvent(userId, id, "ACCOUNT_LOCKED", "LOCKED", "Too many failed step-up attempts", correlationId, now));
            await db.SaveChangesAsync(ct);
            if (user.IsLocked(now))
                throw new KasaException(ErrorKind.Locked, ErrorCodes.AuthenticationLocked, "Too many wrong attempts. Please try again later.");
            // 403, not 401: the session is fine. A 401 would make the app sign the user out.
            var failed = new KasaException(ErrorKind.Forbidden, ErrorCodes.AuthenticationFailed, "That verification was not correct.");
            failed.Data["remainingAttempts"] = user.RemainingAttempts(max);
            throw failed;
        }

        user.RecordSuccessfulAttempt();
        challenge.Consume(now);
        db.AuthenticationAttempts.Add(Attempt(AuthenticationResult.Success, "Step-up succeeded"));
        tx.MarkAuthenticated(AuthorizationFingerprint(tx), proof.Method.ToString(), now, correlationId);
        await db.SaveOrConflictAsync(ct);
        return tx;
    }

    public async Task<Transaction> AuthorizeAsync(Guid userId, Guid id, string correlationId, CancellationToken ct)
    {
        var tx = await LoadForActionAsync(userId, id, correlationId, ct);
        if (tx.State == TransactionState.Authorized) return tx; // replay
        if (tx.State == TransactionState.Authenticated)
        {
            tx.BeginAuthorization(clock.GetUtcNow(), correlationId);
            await db.SaveOrConflictAsync(ct);
        }
        else if (tx.State != TransactionState.AuthorizationPending)
        {
            throw KasaException.Conflict("INVALID_TRANSACTION_TRANSITION", $"A transaction in state {tx.State} cannot be authorized.");
        }

        ConsentOutcome outcome;
        try
        {
            outcome = await provider.RequestConsentAsync(tx.Id, ConsentScope, ct);
        }
        catch (ProviderUnavailableException ex)
        {
            // Stays AUTHORIZATION_PENDING: nothing was sent, and the request can simply be retried.
            logger.LogWarning(ex, "Consent request for {TransactionId} failed: provider unavailable", tx.Id);
            throw new KasaException(ErrorKind.ProviderFailure, ErrorCodes.ProviderUnavailable, "The payment provider is not available right now. Please try again.", retryable: true);
        }

        var now = clock.GetUtcNow();
        var consent = new Consent(userId, tx.Id, provider.Name, ConsentScope, outcome.Status, outcome.ProviderReference, now, outcome.ExpiresAt);
        db.Consents.Add(consent);
        if (!consent.IsUsable(now))
        {
            tx.FailAuthorization("CONSENT_NOT_GRANTED", now, correlationId);
            await db.SaveOrConflictAsync(ct);
            throw new KasaException(ErrorKind.Forbidden, ErrorCodes.ConsentRequired, "The provider did not authorize this transfer.");
        }
        tx.Authorize(consent.Id, outcome.Detail, now, correlationId);
        await db.SaveOrConflictAsync(ct);
        return tx;
    }

    // ------------------------------------------------------------------------------------------ submit

    /// <summary>
    /// The only place a provider is asked to move money. The order of operations is the safety mechanism:
    ///
    ///   1. Re-verify the authorization fingerprint and move to SUBMITTING.
    ///   2. SAVE that to the database *before* calling MTN. The save carries the optimistic-concurrency
    ///      check, so of two simultaneous requests only one gets past this line; the other gets a 409 and
    ///      never reaches MTN. It also means a crash mid-call leaves a durable "we were submitting" marker.
    ///   3. Call MTN, with the transaction id as MTN's own idempotency key (X-Reference-Id).
    ///   4. Persist exactly what we learned: accepted -> SUBMITTED, refused -> FAILED, unknown ->
    ///      UNKNOWN. Uncertainty is never turned into success or failure by guessing, and an uncertain
    ///      submission is never retried blindly: the reconciler asks MTN about the SAME reference.
    /// </summary>
    public async Task<Transaction> SubmitAsync(Guid userId, Guid id, string correlationId, CancellationToken ct)
    {
        var tx = await LoadOwnedAsync(userId, id, ct);

        // Replay after a lost response: submission already began, so just report where it stands.
        if (tx.ProviderReference is not null) return tx;

        await ExpireGuardAsync(tx, correlationId, ct);

        var now = clock.GetUtcNow();
        tx.BeginSubmission(AuthorizationFingerprint(tx), now, correlationId);
        var operation = new ProviderOperation(tx.Id, provider.Name, "TRANSFER", tx.ProviderReference!, "SUBMITTING", null, now);
        db.ProviderOperations.Add(operation);
        await db.SaveOrConflictAsync(ct); // <-- the gate: nothing below runs for the losing request of a race

        SubmissionResult result;
        try
        {
            // CancellationToken.None on purpose: if the phone disconnects mid-request we must still finish the
            // call and record the outcome, because MTN may already have the request.
            result = await provider.SubmitTransferAsync(
                new TransferRequest(tx.Id, tx.ProviderReference!, tx.Amount, Msisdn.FromStored(tx.RecipientMsisdn), "Kasa Sika transfer"),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error submitting transaction {TransactionId}; treating the outcome as uncertain", tx.Id);
            result = SubmissionResult.Uncertain($"unexpected error ({ex.GetType().Name})");
        }

        now = clock.GetUtcNow();
        switch (result.Outcome)
        {
            case SubmissionOutcome.Accepted:
                tx.RecordProviderAccepted(provider.Name, now, correlationId);
                operation.Update("ACCEPTED", null, now);
                break;
            case SubmissionOutcome.Rejected:
                tx.RecordSubmissionRejected(result.Reason ?? "PROVIDER_REJECTED", provider.Name, now, correlationId);
                operation.Update("REJECTED", result.Reason, now);
                outcomes.Record(tx, now);
                break;
            default:
                tx.RecordSubmissionUncertain(result.Reason ?? "unknown", provider.Name, now, correlationId);
                operation.Update("UNCERTAIN", result.Reason, now);
                db.ReconciliationRecords.Add(new ReconciliationRecord(tx.Id, tx.State.ToString(), "UNKNOWN", "OPEN",
                    $"Submission outcome uncertain: {result.Reason}", now));
                break;
        }
        // Not tied to the request's cancellation token: the outcome must be recorded even if the caller hung up.
        await db.SaveChangesAsync(CancellationToken.None);
        return tx;
    }

    /// <summary>After a successful step-up: authorize with the provider and submit. Nothing here needs the user.</summary>
    public async Task<Transaction> AuthorizeAndSubmitAsync(Guid userId, Guid id, string correlationId, CancellationToken ct)
    {
        await AuthorizeAsync(userId, id, correlationId, ct);
        return await SubmitAsync(userId, id, correlationId, ct);
    }

    // ------------------------------------------------------------------------------------------ status / cancel

    public async Task<Transaction> RefreshStatusAsync(Guid userId, Guid id, string correlationId, CancellationToken ct)
    {
        var tx = await LoadOwnedAsync(userId, id, ct);
        if (tx.ExpireIfDue(clock.GetUtcNow(), correlationId))
        {
            await db.SaveOrConflictAsync(ct);
            return tx;
        }
        if (await statusSync.SyncAsync(tx, "user:status-check", correlationId, ct))
            await db.SaveOrConflictAsync(ct);
        return tx;
    }

    public async Task<Transaction> CancelAsync(Guid userId, Guid id, string correlationId, CancellationToken ct)
    {
        var tx = await LoadOwnedAsync(userId, id, ct);
        if (tx.State == TransactionState.Cancelled) return tx; // replay
        tx.Cancel("user", clock.GetUtcNow(), correlationId);
        await db.SaveOrConflictAsync(ct);
        return tx;
    }

    // ------------------------------------------------------------------------------------------ callbacks

    /// <summary>
    /// An incoming provider callback. MTN documents no signature on callbacks, so we treat the payload as an
    /// untrusted hint: it tells us WHICH transaction to look at, and the actual result is then fetched from
    /// MTN over an authenticated request. A forged callback can at worst cause one extra status check, and a
    /// duplicate callback is recognised by its fingerprint and changes nothing.
    /// </summary>
    public async Task<CallbackResult> HandleCallbackAsync(
        string providerName, IReadOnlyDictionary<string, string> headers, string body, string correlationId, CancellationToken ct)
    {
        if (!string.Equals(providerName, provider.Name, StringComparison.OrdinalIgnoreCase))
            throw KasaException.NotFound("PROVIDER_NOT_CONFIGURED", "That provider is not the active payment provider.");

        var callback = provider.ParseCallback(headers, body);
        var tx = await db.Transactions.Include(t => t.Events)
            .FirstOrDefaultAsync(t => t.Provider == provider.Name && t.ProviderReference == callback.ProviderReference, ct);

        // Check first so an ordinary duplicate is not an exception (and not an error line in the logs); the
        // unique index below still catches two identical callbacks arriving at the same instant.
        if (await db.CallbackReceipts.AnyAsync(r => r.Fingerprint == callback.Fingerprint, ct)) return await DuplicateAsync(tx, correlationId, ct);

        var now = clock.GetUtcNow();
        db.CallbackReceipts.Add(new ProviderCallbackReceipt(provider.Name, callback.Fingerprint, callback.ProviderReference, tx?.Id, callback.Summary, now));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (await db.CallbackReceipts.AnyAsync(r => r.Fingerprint == callback.Fingerprint, ct)) return await DuplicateAsync(null, correlationId, ct);
            throw;
        }

        if (tx is null)
        {
            db.AuditEvents.Add(new AuditEvent(null, null, "CALLBACK_UNMATCHED", "IGNORED", "No transaction for the callback's reference", correlationId, now));
            await db.SaveChangesAsync(ct);
            return CallbackResult.Ignored;
        }

        if (await statusSync.SyncAsync(tx, "provider-callback", correlationId, ct))
            await db.SaveOrConflictAsync(ct);
        return CallbackResult.Applied;
    }

    private async Task<CallbackResult> DuplicateAsync(Transaction? tx, string correlationId, CancellationToken ct)
    {
        db.AuditEvents.Add(new AuditEvent(tx?.UserId, tx?.Id, "CALLBACK_DUPLICATE", "IGNORED", "Identical callback already processed", correlationId, clock.GetUtcNow()));
        await db.SaveChangesAsync(ct);
        return CallbackResult.Duplicate;
    }

    // ------------------------------------------------------------------------------------------ helpers

    private Task<Transaction?> FindByKeyAsync(Guid userId, string key, CancellationToken ct) =>
        db.Transactions.Include(t => t.Events).FirstOrDefaultAsync(t => t.UserId == userId && t.IdempotencyKey == key, ct);

    /// <summary>Ownership is part of the query, so another user's transaction is simply "not found" (404, not 403).</summary>
    public async Task<Transaction> LoadOwnedAsync(Guid userId, Guid id, CancellationToken ct) =>
        await db.Transactions.Include(t => t.Events).FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId, ct)
        ?? throw KasaException.NotFound("TRANSACTION_NOT_FOUND", "Transaction not found.");

    private async Task<Transaction> LoadForActionAsync(Guid userId, Guid id, string correlationId, CancellationToken ct)
    {
        var tx = await LoadOwnedAsync(userId, id, ct);
        await ExpireGuardAsync(tx, correlationId, ct);
        return tx;
    }

    /// <summary>Records expiry (and saves it) before reporting it, so the Expired state is not lost with the failed request.</summary>
    private async Task ExpireGuardAsync(Transaction tx, string correlationId, CancellationToken ct)
    {
        if (tx.ExpireIfDue(clock.GetUtcNow(), correlationId)) await db.SaveOrConflictAsync(ct);
        if (tx.State == TransactionState.Expired)
            throw new KasaException(ErrorKind.Gone, "TRANSACTION_EXPIRED", "This transaction expired. Please start again.");
    }

    /// <summary>What a challenge is bound to: who, which transfer, how much, to whom.</summary>
    private string BindingFingerprint(Transaction tx) =>
        protector.Fingerprint($"challenge|{tx.UserId:N}|{tx.Id:N}|{tx.AmountMinor}|{tx.Currency}|{tx.RecipientMsisdn}");

    /// <summary>Everything the user authenticated for. If any of it differs at submission, submission is refused.</summary>
    private string AuthorizationFingerprint(Transaction tx) =>
        protector.Fingerprint($"auth|{tx.UserId:N}|{tx.Id:N}|{tx.AmountMinor}|{tx.Currency}|{tx.RecipientMsisdn}");
}
