using System.Text.Json;
using KasaSika.Application.Abstractions;
using KasaSika.Application.Language;
using KasaSika.Application.Options;
using KasaSika.Application.Speech;
using KasaSika.Application.Transactions;
using KasaSika.Domain;
using KasaSika.Domain.Common;
using KasaSika.Domain.Contacts;
using KasaSika.Domain.Conversations;
using KasaSika.Domain.Identity;
using KasaSika.Domain.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KasaSika.Application.Conversations;

/// <summary>
/// The two-way voice conversation: listen → understand → respond → clarify → confirm → authenticate →
/// execute → report.
///
/// Safety properties, each enforced here or below:
///  - Nothing financial is guessed. Missing amount → ask. Missing recipient → ask. Two "Kofi"s → ask which.
///    Unparsed speech → ask to repeat. Recognition failure → say so; never invent a transcript.
///  - Every transfer is read back (amount, name, last four digits) and needs an explicit "yes".
///  - Speech cannot authenticate: after "yes" the user must pass a transaction-bound step-up challenge.
///  - Money only moves through <see cref="TransactionService"/>'s state machine.
///  - Questions about money ("last transaction", "today's total") run fixed, owner-scoped queries.
/// </summary>
public sealed class ConversationService(
    IKasaDbContext db,
    TransactionService transactions,
    TransactionQueries queries,
    SpeechService speech,
    ILanguageRegistry languages,
    IPaymentProvider provider,
    TimeProvider clock,
    IOptions<LimitsOptions> limits,
    ILogger<ConversationService> logger)
{
    private const string TurnScope = "conversation-turn";
    private static readonly TimeSpan ConversationIdleLimit = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ============================================================================================ entry points

    /// <summary>Opening the app: greet, announce results that arrived while away, and restore the conversation.</summary>
    public async Task<ConversationReply> WelcomeAsync(Guid userId, string? languageCode, string correlationId, CancellationToken ct)
    {
        var user = await LoadUserAsync(userId, ct);
        var language = languages.Get(languageCode ?? user.PreferredLanguage);
        var conversation = await CurrentConversationAsync(userId, null, language.Code, ct);
        conversation.SetLanguage(language.Code);

        var parts = new List<ReplyPart>();
        AuthenticationPrompt? prompt = null;
        parts.Add(ReplyPart.Of(provider.Mode == ProviderMode.Sandbox ? "MODE_SANDBOX" : "MODE_MOCK"));

        var unread = await db.Notifications.Where(n => n.UserId == userId && n.ReadAt == null).OrderBy(n => n.CreatedAt).Take(3).ToListAsync(ct);
        var now = clock.GetUtcNow();
        foreach (var n in unread) n.MarkRead(now);

        // Resume an unfinished step rather than silently discarding it.
        var tx = conversation.TransactionId is { } txId ? await transactions.LoadOwnedAsync(userId, txId, ct) : null;
        if (tx is not null && tx.ExpireIfDue(now, correlationId)) { conversation.Reset(); tx = null; }
        if (conversation.Stage is ConversationStage.AwaitingConfirmation or ConversationStage.AwaitingAuthentication && tx is not null
            && TransactionStateMachine.IsBeforeSubmission(tx.State))
        {
            parts.Add(ReplyPart.Of("BACK_TO_FLOW"));
            parts.AddRange(PromptFor(conversation, tx));
            if (conversation.Stage == ConversationStage.AwaitingAuthentication)
                prompt = await IssuePromptAsync(userId, tx.Id, null, correlationId, ct);
        }
        else
        {
            if (conversation.Stage != ConversationStage.Processing) { conversation.Reset(); tx = null; }
            parts.Add(ReplyPart.Of("WELCOME", new ReplyArgs { Name = user.DisplayName }));
        }

        var reply = await RespondAsync(conversation, parts, heard: null, heardBy: null, tx, prompt, extraText: unread.Select(n => n.Text).ToList(), ct: ct);
        return reply with { Transcript = await TranscriptAsync(conversation.Id, ct) };
    }

    public async Task<ConversationReply> HandleTurnAsync(Guid userId, Guid? deviceId, TurnInput input, string correlationId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.TurnId) || input.TurnId.Length is < 8 or > 64 || !input.TurnId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw KasaException.Validation("TURN_ID_REQUIRED", "Each turn needs a turnId of 8-64 letters, digits or dashes.");

        // Idempotency: a re-sent voice command (dropped connection, double tap) gets the same answer, once.
        var requestFingerprint = $"{input.Language}|{input.Text}|{input.Audio?.Data.Length}";
        var previous = await db.IdempotencyRecords.AsNoTracking()
            .FirstOrDefaultAsync(r => r.UserId == userId && r.Scope == TurnScope && r.Key == input.TurnId, ct);
        if (previous is not null)
        {
            if (previous.RequestFingerprint != requestFingerprint)
                throw KasaException.Conflict(ErrorCodes.DuplicateTransaction, "That turn id was already used for a different request.");
            return JsonSerializer.Deserialize<ConversationReply>(previous.ResponseJson, Json)!;
        }

        var user = await LoadUserAsync(userId, ct);
        var language = languages.Get(input.Language ?? user.PreferredLanguage);
        var conversation = await CurrentConversationAsync(userId, input.ConversationId, language.Code, ct);
        conversation.SetLanguage(language.Code);

        // 1. Hear.
        string? heard = input.Text?.Trim();
        var heardBy = input.RecognizedBy ?? (input.InputMode == "voice" ? "device" : input.InputMode);
        if (input.Audio is not null)
        {
            try
            {
                var result = await speech.TranscribeAsync(input.Audio, language, ct);
                heard = result.Text.Trim();
                heardBy = result.RecognizedBy;
            }
            catch (KasaException ex) when (ex.Code == ErrorCodes.SpeechUnavailable)
            {
                // Never fabricate recognition: say the voice service is down, keep every slot as it was.
                return await FinishTurnAsync(userId, input, requestFingerprint,
                    await RespondAsync(conversation, [ReplyPart.Of("SPEECH_UNAVAILABLE")], null, null, await AttachedTxAsync(conversation, ct), ct: ct), ct);
            }
            catch (KasaException ex) when (ex.Code is "INVALID_AUDIO" or "EMPTY_AUDIO")
            {
                heard = "";
            }
        }
        if (heard is { Length: > TextNormalizer.MaxUtteranceLength }) heard = heard[..TextNormalizer.MaxUtteranceLength];

        await AddTurnAsync(conversation, "user", heard ?? "", language.Code, input.InputMode, heardBy, ct);

        // 2. Understand and act.
        var before = Snapshot(conversation);
        // Stable order: "the second one" must mean the same person every time the list is read out.
        var contacts = await db.Contacts.AsNoTracking().Where(c => c.UserId == userId).OrderBy(c => c.DisplayName).ToListAsync(ct);
        var parsed = IntentParser.Parse(heard, contacts);
        var context = new TurnContext(user, conversation, parsed, contacts, deviceId, input.TurnId, correlationId);

        var parts = await DispatchAsync(context, ct);
        if (context.StageChanged && !context.WentBack) conversation.SaveSnapshot(before);

        var tx = context.Transaction ?? await AttachedTxAsync(conversation, ct);
        var reply = await RespondAsync(conversation, parts, heard, heardBy, tx, context.Authentication, context.Candidates, ct: ct);
        return await FinishTurnAsync(userId, input, requestFingerprint, reply, ct);
    }

    /// <summary>The user answered the step-up challenge (fingerprint or passcode). On success the transfer is sent.</summary>
    public async Task<ConversationReply> AuthenticateAsync(Guid userId, AuthenticateInput input, string correlationId, CancellationToken ct)
    {
        var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.Id == input.ConversationId && c.UserId == userId, ct)
            ?? throw KasaException.NotFound("CONVERSATION_NOT_FOUND", "Conversation not found.");
        if (conversation.Stage != ConversationStage.AwaitingAuthentication || conversation.TransactionId is not { } txId)
            throw KasaException.Conflict("NOT_AWAITING_AUTHENTICATION", "There is no transfer waiting for verification.");

        var parts = new List<ReplyPart>();
        AuthenticationPrompt? prompt = null;
        Transaction tx;
        try
        {
            tx = await transactions.AuthenticateAsync(userId, txId,
                new StepUpProof(input.ChallengeId, input.Method, input.Passcode, input.DeviceId, input.Signature), correlationId, ct);
        }
        catch (KasaException ex) when (ex.Code == ErrorCodes.AuthenticationFailed)
        {
            var remaining = ex.Data["remainingAttempts"] is int r ? r : 0;
            parts.Add(ReplyPart.Of("AUTH_FAILED", new ReplyArgs { Count = remaining }));
            prompt = await IssuePromptAsync(userId, txId, input.DeviceId, correlationId, ct);
            return await RespondAsync(conversation, parts, null, null, await transactions.LoadOwnedAsync(userId, txId, ct), prompt, ct: ct);
        }
        catch (KasaException ex) when (ex.Code == ErrorCodes.AuthenticationLocked)
        {
            var locked = await transactions.LoadOwnedAsync(userId, txId, ct);
            await CancelIfPossibleAsync(userId, locked, correlationId, ct);
            conversation.Reset();
            return await RespondAsync(conversation, [ReplyPart.Of("AUTH_LOCKED")], null, null, locked, ct: ct);
        }
        catch (KasaException ex) when (ex.Code is "CHALLENGE_EXPIRED" or "CHALLENGE_NOT_FOUND")
        {
            prompt = await IssuePromptAsync(userId, txId, input.DeviceId, correlationId, ct);
            return await RespondAsync(conversation, [ReplyPart.Of("AUTH_REQUIRED")], null, null, await transactions.LoadOwnedAsync(userId, txId, ct), prompt, ct: ct);
        }
        catch (KasaException ex) when (ex.Code == "TRANSACTION_EXPIRED")
        {
            conversation.Reset();
            return await RespondAsync(conversation, [ReplyPart.Of("EXPIRED")], null, null, null, ct: ct);
        }

        parts.Add(ReplyPart.Of("PROCESSING"));
        conversation.MoveTo(ConversationStage.Processing);
        try
        {
            tx = await transactions.AuthorizeAndSubmitAsync(userId, tx.Id, correlationId, ct);
        }
        catch (KasaException ex) when (ex.Code is ErrorCodes.ProviderUnavailable or ErrorCodes.ConsentRequired)
        {
            // Nothing reached the provider's transfer endpoint: stop cleanly so nothing lingers half-done.
            var stopped = await transactions.LoadOwnedAsync(userId, tx.Id, ct);
            await CancelIfPossibleAsync(userId, stopped, correlationId, ct);
            conversation.Reset();
            parts.Clear();
            parts.Add(ReplyPart.Of(ex.Code == ErrorCodes.ProviderUnavailable ? "PROVIDER_UNAVAILABLE" : "FAILED", new ReplyArgs { Name = stopped.RecipientName }));
            return await RespondAsync(conversation, parts, null, null, stopped, ct: ct);
        }

        parts.Add(TransactionOutcomes.ResultReply(tx));
        if (TransactionStateMachine.IsFinal(tx.State) || tx.State is TransactionState.Unknown or TransactionState.ReconciliationRequired)
            conversation.MoveTo(ConversationStage.Completed);
        return await RespondAsync(conversation, parts, null, null, tx, ct: ct);
    }

    /// <summary>
    /// Polled by the app while a transfer is processing. Speaks only when something changed, so a user is not
    /// told "processing" every two seconds.
    /// </summary>
    public async Task<ConversationReply> CheckStatusAsync(Guid userId, Guid conversationId, string correlationId, CancellationToken ct)
    {
        var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId && c.UserId == userId, ct)
            ?? throw KasaException.NotFound("CONVERSATION_NOT_FOUND", "Conversation not found.");
        if (conversation.TransactionId is not { } txId)
            return await RespondAsync(conversation, [], null, null, null, ct: ct);

        var tx = await transactions.RefreshStatusAsync(userId, txId, correlationId, ct);
        var parts = new List<ReplyPart>();
        // Announce the outcome exactly once: on leaving PROCESSING. The outcome may have been set by this check,
        // by a provider callback, or by the reconciler between two polls - the user hears it either way.
        if (conversation.Stage == ConversationStage.Processing
            && (TransactionStateMachine.IsFinal(tx.State) || tx.State is TransactionState.Unknown or TransactionState.ReconciliationRequired))
        {
            parts.Add(TransactionOutcomes.ResultReply(tx));
            conversation.MoveTo(ConversationStage.Completed);
            // Heard live, so it does not need announcing again on next launch.
            var now = clock.GetUtcNow();
            foreach (var n in db.Notifications.Local.Where(n => n.TransactionId == tx.Id)) n.MarkRead(now);
            foreach (var n in await db.Notifications.Where(n => n.TransactionId == tx.Id && n.ReadAt == null).ToListAsync(ct)) n.MarkRead(now);
        }
        return await RespondAsync(conversation, parts, null, null, tx, ct: ct);
    }

    public async Task<ConversationReply> ResetAsync(Guid userId, Guid conversationId, string correlationId, CancellationToken ct)
    {
        var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId && c.UserId == userId, ct)
            ?? throw KasaException.NotFound("CONVERSATION_NOT_FOUND", "Conversation not found.");
        var tx = await AttachedTxAsync(conversation, ct);
        if (tx is not null) await CancelIfPossibleAsync(userId, tx, correlationId, ct);
        conversation.Reset();
        return await RespondAsync(conversation, [ReplyPart.Of("STARTED_AGAIN"), ReplyPart.Of("ASK_WHAT")], null, null, null, ct: ct);
    }

    // ============================================================================================ dispatch

    private sealed class TurnContext(User user, Conversation conversation, ParsedUtterance parsed, IReadOnlyList<Contact> contacts, Guid? deviceId, string turnId, string correlationId)
    {
        public User User { get; } = user;
        public Conversation Conversation { get; } = conversation;
        public ParsedUtterance Parsed { get; } = parsed;
        public IReadOnlyList<Contact> Contacts { get; } = contacts;
        public Guid? DeviceId { get; } = deviceId;
        public string TurnId { get; } = turnId;
        public string CorrelationId { get; } = correlationId;
        public ConversationStage StartStage { get; } = conversation.Stage;
        public Transaction? Transaction { get; set; }
        public AuthenticationPrompt? Authentication { get; set; }
        public List<CandidateView>? Candidates { get; set; }
        public bool WentBack { get; set; }
        public bool StageChanged => Conversation.Stage != StartStage;
    }

    private async Task<List<ReplyPart>> DispatchAsync(TurnContext c, CancellationToken ct)
    {
        var p = c.Parsed;
        var conv = c.Conversation;
        if (p.IsEmpty) return [ReplyPart.Of("NO_SPEECH"), .. await CurrentPromptAsync(c, ct)];

        // A new or changed amount/recipient is content, not a control word: "no, 20 cedis" changes the amount.
        var hasContent = p.AmountMinor is not null || p.HasRecipient;

        if (p.Has(Command.Repeat) && !hasContent) return Replay(conv);
        if (p.Has(Command.Help)) return [ReplyPart.Of(HelpKey(conv.Stage))];
        if (p.Has(Command.StartAgain)) return await StartAgainAsync(c, ct);
        if (p.Has(Command.GoBack) && !hasContent) return await GoBackAsync(c, ct);
        if (p.Has(Command.Cancel) || (p.Has(Command.Deny) && !hasContent && IsMidFlow(conv.Stage) && conv.Stage != ConversationStage.ChoosingRecipient))
            return await CancelFlowAsync(c, ct);

        // Questions answered by fixed, owner-scoped queries - never by free-form generation. "How much did I
        // send today?" contains "send" but no amount or recipient: it is a question, not a transfer.
        if (!p.LooksLikeSend || (p.AmountMinor is null && !p.HasRecipient))
        {
            var info = await AnswerQuestionAsync(c, ct);
            if (info is not null)
            {
                if (IsMidFlow(conv.Stage)) { info.Add(ReplyPart.Of("BACK_TO_FLOW")); info.AddRange(await CurrentPromptAsync(c, ct)); }
                return info;
            }
        }

        switch (conv.Stage)
        {
            case ConversationStage.AwaitingConfirmation:
                return await OnConfirmationAsync(c, ct);
            case ConversationStage.AwaitingAuthentication:
                c.Authentication = await IssuePromptAsync(c.User.Id, conv.TransactionId!.Value, c.DeviceId, c.CorrelationId, ct);
                return [ReplyPart.Of("AUTH_WAITING")];
            case ConversationStage.Processing:
                // A transfer still waiting for MTN must not block the user: a new request starts a new flow, and
                // the pending one is still resolved by the reconciler and announced as a notification.
                if (p.LooksLikeSend && hasContent) goto default;
                return await StatusAnswerAsync(c, ct) ?? [ReplyPart.Of("PENDING")];
            case ConversationStage.ChoosingRecipient:
                return await OnChoosingAsync(c, ct);
            case ConversationStage.AwaitingAmount:
                if (p.AmountMinor is { } amount) conv.SetAmount(amount);
                if (p.HasRecipient && !ApplyRecipient(c)) return RecipientProblem(c);
                if (p.AmountMinor is null && !p.HasRecipient) return [ReplyPart.Of("NOT_UNDERSTOOD"), .. await CurrentPromptAsync(c, ct)];
                return await ProceedAsync(c, ct);
            case ConversationStage.AwaitingRecipient:
                if (p.AmountMinor is { } a2) conv.SetAmount(a2);
                if (p.HasRecipient && !ApplyRecipient(c)) return RecipientProblem(c);
                if (!p.HasRecipient && p.AmountMinor is null) return [ReplyPart.Of("NOT_UNDERSTOOD"), .. await CurrentPromptAsync(c, ct)];
                return await ProceedAsync(c, ct);
            default: // Idle / Completed: a new request
                if (!p.LooksLikeSend && !hasContent)
                    return p.Has(Command.Confirm) || p.Has(Command.Deny) ? [ReplyPart.Of("ASK_WHAT")] : [ReplyPart.Of("NOT_UNDERSTOOD")];
                conv.Reset();
                conv.SetAmount(p.AmountMinor);
                if (p.HasRecipient && !ApplyRecipient(c)) return RecipientProblem(c);
                return await ProceedAsync(c, ct);
        }
    }

    /// <summary>Fills whatever is missing, or creates the transfer and reads it back for confirmation.</summary>
    private async Task<List<ReplyPart>> ProceedAsync(TurnContext c, CancellationToken ct)
    {
        var conv = c.Conversation;
        if (conv.Stage == ConversationStage.ChoosingRecipient && conv.PendingContactId is null && conv.Candidates.Count > 0)
            return ChooseRecipientPrompt(c);

        Contact? contact = conv.PendingContactId is { } id ? c.Contacts.FirstOrDefault(x => x.Id == id) : null;
        Msisdn? number = contact?.Number ?? (conv.PendingRecipientQuery is { } q && Msisdn.TryParse(q, out var n) ? n : null);

        if (conv.PendingAmountMinor is null)
        {
            conv.MoveTo(ConversationStage.AwaitingAmount);
            return [ReplyPart.Of("ASK_AMOUNT", new ReplyArgs { Name = contact?.DisplayName ?? NumberName(number) ?? "them" })];
        }
        if (number is null)
        {
            conv.MoveTo(ConversationStage.AwaitingRecipient);
            return [ReplyPart.Of("ASK_RECIPIENT", new ReplyArgs { AmountMinor = conv.PendingAmountMinor })];
        }
        if (conv.PendingAmountMinor > limits.Value.MaxTransactionMinor)
        {
            var tooMuch = conv.PendingAmountMinor;
            conv.SetAmount(null);
            conv.MoveTo(ConversationStage.AwaitingAmount);
            return [ReplyPart.Of("AMOUNT_TOO_HIGH", new ReplyArgs { AmountMinor = tooMuch, LimitMinor = limits.Value.MaxTransactionMinor })];
        }

        var name = contact?.DisplayName ?? NumberName(number)!;
        Transaction tx;
        try
        {
            tx = await transactions.CreateAsync(new CreateTransfer(
                c.User.Id, $"conv-{conv.Id:N}-{c.TurnId}", Money.FromMinor(conv.PendingAmountMinor.Value, "GHS"), name, number,
                contact?.Id, conv.Language, conv.Id), c.CorrelationId, ct);
        }
        catch (KasaException ex) when (ex.Code == "DAILY_LIMIT_EXCEEDED")
        {
            conv.Reset();
            return [ReplyPart.Of("DAILY_LIMIT")];
        }
        catch (KasaException ex) when (ex.Code == ErrorCodes.ProviderUnavailable)
        {
            conv.Reset();
            return [ReplyPart.Of("PROVIDER_UNAVAILABLE")];
        }

        c.Transaction = tx;
        if (tx.State == TransactionState.Failed)
        {
            conv.Reset();
            return [ReplyPart.Of("RECIPIENT_INACTIVE", new ReplyArgs { Name = name })];
        }
        conv.AttachTransaction(tx.Id);
        conv.MoveTo(ConversationStage.AwaitingConfirmation);
        return [ReplyPart.Of("CONFIRM_SEND", new ReplyArgs { AmountMinor = tx.AmountMinor, Name = tx.RecipientName, LastFour = tx.RecipientMasked[^4..] })];
    }

    private async Task<List<ReplyPart>> OnConfirmationAsync(TurnContext c, CancellationToken ct)
    {
        var p = c.Parsed;
        var conv = c.Conversation;
        var tx = await AttachedTxAsync(conv, ct);
        if (tx is null || !TransactionStateMachine.IsBeforeSubmission(tx.State))
        {
            conv.Reset();
            return [ReplyPart.Of(tx?.State == TransactionState.Expired ? "EXPIRED" : "ASK_WHAT")];
        }

        // "Send 20 cedis instead" / "No, to Kwame": a correction. The old draft is cancelled, a new one read back.
        var newAmount = p.AmountMinor is { } a && a != tx.AmountMinor;
        var newRecipient = p.HasRecipient && !(p.RecipientMatches.Count == 1 && p.RecipientMatches[0].Contact.Id == tx.RecipientContactId);
        if (newAmount || newRecipient)
        {
            await CancelIfPossibleAsync(c.User.Id, tx, c.CorrelationId, ct);
            conv.AttachTransaction(null);
            if (newAmount) conv.SetAmount(p.AmountMinor);
            if (newRecipient && !ApplyRecipient(c)) return RecipientProblem(c);
            var next = await ProceedAsync(c, ct);
            return [ReplyPart.Of("CHANGED"), .. next];
        }

        if (p.Has(Command.Confirm) && !p.Has(Command.Deny))
        {
            try
            {
                tx = await transactions.ConfirmAsync(c.User.Id, tx.Id, c.CorrelationId, ct);
            }
            catch (KasaException ex) when (ex.Code == "TRANSACTION_EXPIRED")
            {
                conv.Reset();
                return [ReplyPart.Of("EXPIRED")];
            }
            c.Authentication = await IssuePromptAsync(c.User.Id, tx.Id, c.DeviceId, c.CorrelationId, ct);
            c.Transaction = await transactions.LoadOwnedAsync(c.User.Id, tx.Id, ct);
            conv.MoveTo(ConversationStage.AwaitingAuthentication);
            return [ReplyPart.Of("AUTH_REQUIRED")];
        }
        return [ReplyPart.Of("CONFIRM_EXPECTED")];
    }

    private async Task<List<ReplyPart>> OnChoosingAsync(TurnContext c, CancellationToken ct)
    {
        var conv = c.Conversation;
        var p = c.Parsed;
        var candidates = conv.Candidates.Select(id => c.Contacts.FirstOrDefault(x => x.Id == id)).OfType<Contact>().ToList();
        if (p.AmountMinor is { } a && p.AmountMinor > candidates.Count * 100) conv.SetAmount(a); // "no, 20 cedis" while choosing

        Contact? chosen = null;
        var choice = p.Ordinal ?? p.Numbers.Select(n => (int?)n.WholeValue).FirstOrDefault(v => v >= 1 && v <= candidates.Count);
        if (choice is { } i && i >= 1 && i <= candidates.Count) chosen = candidates[i - 1];
        // "Mensah" / "Kofi Boateng": a fuller name that fits exactly one candidate.
        chosen ??= p.RecipientMatches.Select(m => m.Contact).Where(m => candidates.Any(x => x.Id == m.Id)).ToList() is { Count: 1 } one ? one[0] : null;
        // "the one ending 2233"
        chosen ??= candidates.FirstOrDefault(x => p.Normalized.Replace(" ", "").Contains(x.LastFour, StringComparison.Ordinal));

        if (chosen is null)
        {
            c.Candidates = CandidateViews(candidates);
            return [ReplyPart.Of("CHOICE_NOT_UNDERSTOOD", new ReplyArgs { Options = Options(candidates) })];
        }
        conv.SetContact(chosen.Id);
        conv.MoveTo(ConversationStage.Idle);
        return await ProceedAsync(c, ct);
    }

    /// <summary>Resolves the spoken recipient into the conversation's slots. False if it could not (ambiguous / unknown).</summary>
    private bool ApplyRecipient(TurnContext c)
    {
        var p = c.Parsed;
        var conv = c.Conversation;
        if (p.PhoneNumber is { } phone)
        {
            var known = c.Contacts.FirstOrDefault(x => x.Msisdn == phone.International);
            if (known is not null) conv.SetContact(known.Id);
            else { conv.SetContact(null); conv.SetRecipientQuery(phone.International); }
            return true;
        }
        if (p.RecipientMatches.Count == 1)
        {
            conv.SetContact(p.RecipientMatches[0].Contact.Id);
            conv.SetRecipientQuery(null);
            return true;
        }
        if (p.RecipientMatches.Count > 1)
        {
            // Never guess between two people.
            conv.SetContact(null);
            conv.OfferCandidates(p.RecipientMatches.Select(m => m.Contact.Id));
            conv.SetRecipientQuery(FirstName(p.RecipientMatches[0].Contact.DisplayName));
            conv.MoveTo(ConversationStage.ChoosingRecipient);
            return false;
        }
        conv.SetContact(null);
        conv.SetRecipientQuery(p.UnmatchedRecipientName);
        conv.MoveTo(ConversationStage.AwaitingRecipient);
        return false;
    }

    private List<ReplyPart> RecipientProblem(TurnContext c) =>
        c.Conversation.Stage == ConversationStage.ChoosingRecipient
            ? ChooseRecipientPrompt(c)
            : [ReplyPart.Of("RECIPIENT_UNKNOWN", new ReplyArgs { Name = c.Conversation.PendingRecipientQuery ?? "that person" })];

    private List<ReplyPart> ChooseRecipientPrompt(TurnContext c)
    {
        var candidates = c.Conversation.Candidates.Select(id => c.Contacts.FirstOrDefault(x => x.Id == id)).OfType<Contact>().ToList();
        c.Candidates = CandidateViews(candidates);
        return [ReplyPart.Of("CHOOSE_RECIPIENT", new ReplyArgs
        {
            Count = candidates.Count, Name = c.Conversation.PendingRecipientQuery ?? FirstName(candidates[0].DisplayName), Options = Options(candidates),
        })];
    }

    private async Task<List<ReplyPart>?> AnswerQuestionAsync(TurnContext c, CancellationToken ct)
    {
        var p = c.Parsed;
        var userId = c.User.Id;
        if (p.Has(Command.LastTransaction))
        {
            var last = await queries.LastSubmittedAsync(userId, ct);
            return last is null ? [ReplyPart.Of("NO_TX")] :
            [
                ReplyPart.Of("LAST_TX", new ReplyArgs
                {
                    AmountMinor = last.AmountMinor, Name = last.RecipientName, Date = last.CreatedAt, Status = last.State.ToString(),
                }),
            ];
        }
        if (p.Has(Command.TodayTotal))
        {
            var (amount, count) = await queries.SentTodayAsync(userId, ct);
            return count == 0 ? [ReplyPart.Of("TODAY_NONE")] : [ReplyPart.Of("TODAY_TOTAL", new ReplyArgs { AmountMinor = amount, Count = count })];
        }
        if (p.Has(Command.Balance))
        {
            // Honest capability check: MTN Disbursements pays from Kasa Sika's account and cannot see the user's wallet.
            return provider.Capabilities.UserWalletBalance ? null : [ReplyPart.Of("BALANCE_UNAVAILABLE")];
        }
        if (p.Has(Command.Status) && c.Conversation.Stage is not ConversationStage.AwaitingConfirmation)
            return await StatusAnswerAsync(c, ct) ?? [ReplyPart.Of("NO_TX")];
        return null;
    }

    private async Task<List<ReplyPart>?> StatusAnswerAsync(TurnContext c, CancellationToken ct)
    {
        var inFlight = c.Conversation.TransactionId is { } id && c.Conversation.Stage == ConversationStage.Processing
            ? await transactions.LoadOwnedAsync(c.User.Id, id, ct)
            : await queries.LatestInFlightAsync(c.User.Id, ct) ?? await queries.LastSubmittedAsync(c.User.Id, ct);
        if (inFlight is null) return null;
        var tx = TransactionStateMachine.NeedsProviderResolution(inFlight.State)
            ? await transactions.RefreshStatusAsync(c.User.Id, inFlight.Id, c.CorrelationId, ct)
            : inFlight;
        c.Transaction = tx;
        if (c.Conversation.Stage == ConversationStage.Processing && (TransactionStateMachine.IsFinal(tx.State) || tx.State == TransactionState.Unknown))
            c.Conversation.MoveTo(ConversationStage.Completed);
        return [ReplyPart.Of("STATUS_NOW", new ReplyArgs { AmountMinor = tx.AmountMinor, Name = tx.RecipientName, Status = tx.State.ToString() })];
    }

    private async Task<List<ReplyPart>> CancelFlowAsync(TurnContext c, CancellationToken ct)
    {
        var conv = c.Conversation;
        var tx = await AttachedTxAsync(conv, ct);
        if (tx is not null && !TransactionStateMachine.IsBeforeSubmission(tx.State) && conv.Stage == ConversationStage.Processing)
            return [ReplyPart.Of("CANNOT_CANCEL")];
        if (tx is null && !IsMidFlow(conv.Stage)) return [ReplyPart.Of("NOTHING_TO_CANCEL")];
        if (tx is not null) c.Transaction = await CancelIfPossibleAsync(c.User.Id, tx, c.CorrelationId, ct);
        conv.Reset();
        return [ReplyPart.Of("CANCELLED")];
    }

    private async Task<List<ReplyPart>> StartAgainAsync(TurnContext c, CancellationToken ct)
    {
        var conv = c.Conversation;
        var tx = await AttachedTxAsync(conv, ct);
        if (tx is not null && conv.Stage == ConversationStage.Processing && !TransactionStateMachine.IsFinal(tx.State))
            return [ReplyPart.Of("CANNOT_CANCEL")];
        if (tx is not null) c.Transaction = await CancelIfPossibleAsync(c.User.Id, tx, c.CorrelationId, ct);
        conv.Reset();
        return [ReplyPart.Of("STARTED_AGAIN"), ReplyPart.Of("ASK_WHAT")];
    }

    private async Task<List<ReplyPart>> GoBackAsync(TurnContext c, CancellationToken ct)
    {
        var conv = c.Conversation;
        if (conv.Stage == ConversationStage.Processing) return [ReplyPart.Of("CANNOT_CANCEL")];
        var tx = await AttachedTxAsync(conv, ct);
        if (conv.PreviousSnapshot is null) return [ReplyPart.Of("NOTHING_TO_GO_BACK"), ReplyPart.Of("ASK_WHAT")];

        if (tx is not null) c.Transaction = await CancelIfPossibleAsync(c.User.Id, tx, c.CorrelationId, ct);
        Restore(conv, conv.PreviousSnapshot);
        conv.ClearSnapshot();
        c.WentBack = true;
        return conv.Stage switch
        {
            ConversationStage.AwaitingConfirmation => await ProceedAsync(c, ct), // a fresh draft, read back again
            ConversationStage.ChoosingRecipient => ChooseRecipientPrompt(c),
            _ => await CurrentPromptAsync(c, ct),
        };
    }

    private async Task<List<ReplyPart>> CurrentPromptAsync(TurnContext c, CancellationToken ct) =>
        PromptFor(c.Conversation, await AttachedTxAsync(c.Conversation, ct), c);

    /// <summary>The question that is currently open, so it can be asked again after a detour.</summary>
    private List<ReplyPart> PromptFor(Conversation conv, Transaction? tx, TurnContext? c = null) => conv.Stage switch
    {
        ConversationStage.AwaitingAmount => [ReplyPart.Of("ASK_AMOUNT", new ReplyArgs { Name = ContactName(conv, c) ?? "them" })],
        ConversationStage.AwaitingRecipient => [ReplyPart.Of("ASK_RECIPIENT", new ReplyArgs { AmountMinor = conv.PendingAmountMinor })],
        ConversationStage.ChoosingRecipient when c is not null => ChooseRecipientPrompt(c),
        ConversationStage.AwaitingConfirmation when tx is not null =>
            [ReplyPart.Of("CONFIRM_SEND", new ReplyArgs { AmountMinor = tx.AmountMinor, Name = tx.RecipientName, LastFour = tx.RecipientMasked[^4..] })],
        ConversationStage.AwaitingAuthentication => [ReplyPart.Of("AUTH_REQUIRED")],
        ConversationStage.Processing => [ReplyPart.Of("PENDING")],
        _ => [ReplyPart.Of("ASK_WHAT")],
    };

    private static List<ReplyPart> Replay(Conversation conv)
    {
        if (conv.LastReplyKey is null) return [ReplyPart.Of("ASK_WHAT")];
        // Replay the substance, not the transitions ("Okay, I have changed it." is not worth hearing twice).
        var parts = JsonSerializer.Deserialize<List<ReplyPart>>(conv.LastReplyArgs ?? "[]", Json)?
            .Where(p => p.Key is not ("CHANGED" or "BACK_TO_FLOW" or "MODE_MOCK" or "MODE_SANDBOX")).ToList();
        return parts is { Count: > 0 } ? parts : [ReplyPart.Of("ASK_WHAT")];
    }

    // ============================================================================================ helpers

    private async Task<ConversationReply> RespondAsync(
        Conversation conversation, List<ReplyPart> parts, string? heard, string? heardBy, Transaction? tx,
        AuthenticationPrompt? authentication = null, List<CandidateView>? candidates = null, List<string>? extraText = null, CancellationToken ct = default)
    {
        var language = languages.Get(conversation.Language);
        var now = clock.GetUtcNow();
        conversation.Touch(now);

        SpeechOutput? speechOut = null;
        string? key = null;
        if (parts.Count > 0 || extraText is { Count: > 0 })
        {
            var text = string.Join(" ", (extraText ?? []).Concat([ResponseCatalog.Render(parts, language.Code, clock)]).Where(s => s.Length > 0));
            var english = language.Code == "en" ? null
                : ResponseCatalog.TwiVoiceUnavailableEnglish + ResponseCatalog.Render(parts, "en", clock);
            speechOut = speech.Prepare(text, language, english);
            key = parts.LastOrDefault()?.Key;
            // "Repeat" replays the last thing that was actually said - but a replay itself is not re-recorded.
            if (parts.Count > 0) conversation.Remember(key!, JsonSerializer.Serialize(parts, Json));
            await AddTurnAsync(conversation, "assistant", text, language.Code, "system", null, ct);
        }

        await db.SaveOrConflictAsync(ct);

        return new ConversationReply(
            conversation.Id,
            conversation.Stage,
            conversation.Language,
            heard,
            heardBy,
            Understood(conversation, tx),
            speechOut,
            key,
            parts.Select(p => p.Key).ToList(),
            Expecting(conversation.Stage),
            authentication,
            tx is null ? null : TransactionMapper.ToView(tx, language.Code, clock),
            candidates,
            ListenNext(conversation.Stage, parts),
            conversation.Stage == ConversationStage.Processing ? 1500 : null);
    }

    private async Task<ConversationReply> FinishTurnAsync(Guid userId, TurnInput input, string fingerprint, ConversationReply reply, CancellationToken ct)
    {
        db.IdempotencyRecords.Add(IdempotencyRecord.Create(userId, TurnScope, input.TurnId, fingerprint, JsonSerializer.Serialize(reply, Json), clock.GetUtcNow()));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // The same turn raced itself; the first one's reply stands. Nothing else to undo: the turn's
            // effects were already saved and are idempotent (e.g. the transfer's own idempotency key).
            logger.LogInformation(ex, "Duplicate conversation turn {TurnId}", input.TurnId);
        }
        return reply;
    }

    private async Task<AuthenticationPrompt> IssuePromptAsync(Guid userId, Guid txId, Guid? deviceId, string correlationId, CancellationToken ct)
    {
        var challenge = await transactions.IssueChallengeAsync(userId, txId, deviceId, correlationId, ct);
        return new AuthenticationPrompt(challenge.ChallengeId, challenge.TransactionId, challenge.ExpiresAt, challenge.Methods, challenge.SigningMessage);
    }

    private async Task<Transaction> CancelIfPossibleAsync(Guid userId, Transaction tx, string correlationId, CancellationToken ct)
    {
        if (!TransactionStateMachine.IsBeforeSubmission(tx.State)) return tx;
        return await transactions.CancelAsync(userId, tx.Id, correlationId, ct);
    }

    private async Task<Transaction?> AttachedTxAsync(Conversation conv, CancellationToken ct) =>
        conv.TransactionId is { } id ? await transactions.LoadOwnedAsync(conv.UserId, id, ct) : null;

    private async Task<User> LoadUserAsync(Guid userId, CancellationToken ct) =>
        await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
        ?? throw KasaException.Unauthenticated("UNAUTHENTICATED", "Please sign in again.");

    private async Task<Conversation> CurrentConversationAsync(Guid userId, Guid? conversationId, string language, CancellationToken ct)
    {
        Conversation? conv = null;
        if (conversationId is { } id)
            conv = await db.Conversations.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, ct);
        conv ??= await db.Conversations.Where(c => c.UserId == userId).OrderByDescending(c => c.UpdatedAt).FirstOrDefaultAsync(ct);
        var now = clock.GetUtcNow();
        if (conv is null || (now - conv.UpdatedAt > ConversationIdleLimit && conv.Stage != ConversationStage.Processing))
        {
            conv = Conversation.Start(userId, language, now);
            db.Conversations.Add(conv);
        }
        return conv;
    }

    private async Task AddTurnAsync(Conversation conv, string role, string text, string language, string inputMode, string? recognizedBy, CancellationToken ct)
    {
        var sequence = await db.ConversationTurns.CountAsync(t => t.ConversationId == conv.Id, ct)
                       + db.ConversationTurns.Local.Count(t => t.ConversationId == conv.Id && db.ConversationTurns.Entry(t).State == EntityState.Added);
        db.ConversationTurns.Add(ConversationTurn.Create(conv.Id, sequence + 1, role, text, language, inputMode, recognizedBy, clock.GetUtcNow()));
    }

    private async Task<IReadOnlyList<TurnView>> TranscriptAsync(Guid conversationId, CancellationToken ct) =>
        (await db.ConversationTurns.AsNoTracking().Where(t => t.ConversationId == conversationId)
            .OrderByDescending(t => t.Sequence).Take(20).ToListAsync(ct))
        .OrderBy(t => t.Sequence)
        .Select(t => new TurnView(t.Role, t.Text, t.Language, t.InputMode, t.CreatedAt)).ToList();

    private UnderstoodView? Understood(Conversation conv, Transaction? tx)
    {
        if (tx is not null && conv.TransactionId == tx.Id)
            return new UnderstoodView("SEND_MONEY", tx.AmountMinor, ResponseCatalog.Amount(tx.AmountMinor, "en"), tx.RecipientName, tx.RecipientMasked);
        if (conv.PendingAmountMinor is null && conv.PendingContactId is null && conv.PendingRecipientQuery is null) return null;
        var contact = conv.PendingContactId is { } id ? db.Contacts.Local.FirstOrDefault(x => x.Id == id) ?? db.Contacts.AsNoTracking().FirstOrDefault(x => x.Id == id) : null;
        return new UnderstoodView("SEND_MONEY", conv.PendingAmountMinor,
            conv.PendingAmountMinor is { } a ? ResponseCatalog.Amount(a, "en") : null,
            contact?.DisplayName ?? conv.PendingRecipientQuery, contact?.MsisdnMasked);
    }

    private static IReadOnlyList<string> Expecting(ConversationStage stage) => stage switch
    {
        ConversationStage.AwaitingConfirmation => ["CONFIRM", "CANCEL", "REPEAT", "HELP", "GO_BACK"],
        ConversationStage.AwaitingAuthentication => ["AUTHENTICATE", "CANCEL", "REPEAT", "HELP"],
        ConversationStage.ChoosingRecipient => ["CHOOSE", "CANCEL", "REPEAT", "HELP"],
        ConversationStage.AwaitingAmount or ConversationStage.AwaitingRecipient => ["ANSWER", "CANCEL", "REPEAT", "HELP", "GO_BACK"],
        ConversationStage.Processing => ["STATUS", "REPEAT"],
        _ => ["SPEAK", "HELP"],
    };

    private static bool ListenNext(ConversationStage stage, List<ReplyPart> parts) =>
        stage is ConversationStage.AwaitingAmount or ConversationStage.AwaitingRecipient or ConversationStage.ChoosingRecipient or ConversationStage.AwaitingConfirmation
        || parts.Any(p => p.Key is "NOT_UNDERSTOOD" or "NO_SPEECH" or "ASK_WHAT" or "RECIPIENT_UNKNOWN" or "WELCOME");

    private static bool IsMidFlow(ConversationStage stage) =>
        stage is ConversationStage.AwaitingAmount or ConversationStage.AwaitingRecipient or ConversationStage.ChoosingRecipient
            or ConversationStage.AwaitingConfirmation or ConversationStage.AwaitingAuthentication;

    private static string HelpKey(ConversationStage stage) => stage switch
    {
        ConversationStage.AwaitingConfirmation => "HELP_CONFIRM",
        ConversationStage.AwaitingAuthentication => "HELP_AUTH",
        ConversationStage.ChoosingRecipient => "HELP_CHOOSE",
        _ => "HELP_IDLE",
    };

    private static string? NumberName(Msisdn? number) => number is null ? null : $"the number ending {number.LastFour}";

    private string? ContactName(Conversation conv, TurnContext? c) =>
        conv.PendingContactId is { } id ? (c?.Contacts.FirstOrDefault(x => x.Id == id)?.DisplayName) : conv.PendingRecipientQuery;

    private static string FirstName(string name) => name.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

    private static List<ReplyOption> Options(IEnumerable<Contact> contacts) => contacts.Select(x => new ReplyOption(x.DisplayName, x.LastFour)).ToList();

    private static List<CandidateView> CandidateViews(IEnumerable<Contact> contacts) =>
        contacts.Select((x, i) => new CandidateView(i + 1, x.DisplayName, x.MsisdnMasked)).ToList();

    private sealed record SnapshotData(ConversationStage Stage, long? Amount, Guid? ContactId, string? Query, string? Candidates);

    private static string Snapshot(Conversation conv) =>
        JsonSerializer.Serialize(new SnapshotData(conv.Stage, conv.PendingAmountMinor, conv.PendingContactId, conv.PendingRecipientQuery, conv.CandidateContactIds), Json);

    private static void Restore(Conversation conv, string json)
    {
        var s = JsonSerializer.Deserialize<SnapshotData>(json, Json)!;
        conv.Reset();
        conv.SetAmount(s.Amount);
        conv.SetRecipientQuery(s.Query);
        if (s.Candidates is { Length: > 0 }) conv.OfferCandidates(s.Candidates.Split(',').Select(Guid.Parse));
        if (s.ContactId is not null) conv.SetContact(s.ContactId);
        conv.MoveTo(s.Stage);
    }
}
