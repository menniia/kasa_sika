using KasaSika.Application.Abstractions;
using KasaSika.Application.Conversations;
using KasaSika.Application.Transactions;
using KasaSika.Domain;
using KasaSika.Domain.Conversations;
using KasaSika.Domain.Identity;
using KasaSika.Domain.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KasaSika.UnitTests.Integration;

[Collection("database")]
public class TransactionFlowTests(DatabaseFixture fixture)
{
    private static async Task<Guid> DemoUserIdAsync(IServiceProvider sp)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IKasaDbContext>();
        return await db.Users.Where(u => u.IsDemo).Select(u => u.Id).SingleAsync();
    }

    private static async Task<Transaction> AuthorizedTransferAsync(IServiceProvider sp, Guid userId, long amountMinor = 5000)
    {
        using var scope = sp.CreateScope();
        var tx = scope.ServiceProvider.GetRequiredService<TransactionService>();
        var created = await tx.CreateAsync(new CreateTransfer(userId, $"test-{Guid.NewGuid():N}", Money.FromMinor(amountMinor, "GHS"),
            "Ama Owusu", Msisdn.Parse("0240000111"), null, "en", null), "c", default);
        await tx.ConfirmAsync(userId, created.Id, "c", default);
        var challenge = await tx.IssueChallengeAsync(userId, created.Id, null, "c", default);
        await tx.AuthenticateAsync(userId, created.Id, new StepUpProof(challenge.ChallengeId, AuthenticationMethod.Passcode, DatabaseFixture.DemoPasscode, null, null), "c", default);
        return await tx.AuthorizeAsync(userId, created.Id, "c", default);
    }

    [Fact]
    public async Task Duplicate_submission_reaches_the_provider_exactly_once()
    {
        var provider = new ScriptedProvider { SubmitDelay = TimeSpan.FromMilliseconds(300) };
        var sp = fixture.Build(s => s.ReplaceProvider(provider));
        var userId = await DemoUserIdAsync(sp);
        var tx = await AuthorizedTransferAsync(sp, userId);

        // Two "taps" (or a retry and a voice command) at the same moment, each with its own DbContext.
        async Task<object> Submit()
        {
            using var scope = sp.CreateScope();
            try { return await scope.ServiceProvider.GetRequiredService<TransactionService>().SubmitAsync(userId, tx.Id, "c", default); }
            catch (KasaException ex) { return ex.Code; }
        }
        var results = await Task.WhenAll(Submit(), Submit(), Submit());

        Assert.Equal(1, provider.Submissions);
        Assert.Contains(results, r => r is Transaction { State: TransactionState.Submitted });

        // And a later replay reports the existing state instead of submitting again.
        using var scope2 = sp.CreateScope();
        var again = await scope2.ServiceProvider.GetRequiredService<TransactionService>().SubmitAsync(userId, tx.Id, "c", default);
        Assert.Equal(TransactionState.Submitted, again.State);
        Assert.Equal(1, provider.Submissions);
    }

    [Fact]
    public async Task Same_idempotency_key_returns_the_same_transfer_and_refuses_a_different_one()
    {
        var sp = fixture.Build();
        var userId = await DemoUserIdAsync(sp);
        var key = $"idem-{Guid.NewGuid():N}";
        CreateTransfer Command(long minor) => new(userId, key, Money.FromMinor(minor, "GHS"), "Ama Owusu", Msisdn.Parse("0240000111"), null, "en", null);

        using var s1 = sp.CreateScope();
        var first = await s1.ServiceProvider.GetRequiredService<TransactionService>().CreateAsync(Command(5000), "c", default);
        using var s2 = sp.CreateScope();
        var second = await s2.ServiceProvider.GetRequiredService<TransactionService>().CreateAsync(Command(5000), "c", default);
        Assert.Equal(first.Id, second.Id);

        using var s3 = sp.CreateScope();
        var ex = await Assert.ThrowsAsync<KasaException>(() => s3.ServiceProvider.GetRequiredService<TransactionService>().CreateAsync(Command(6000), "c", default));
        Assert.Equal(ErrorCodes.DuplicateTransaction, ex.Code);
    }

    [Fact]
    public async Task Duplicate_callback_is_recorded_once_and_changes_nothing()
    {
        var provider = new ScriptedProvider();
        var sp = fixture.Build(s => s.ReplaceProvider(provider));
        var userId = await DemoUserIdAsync(sp);
        var tx = await AuthorizedTransferAsync(sp, userId);
        using (var scope = sp.CreateScope())
            await scope.ServiceProvider.GetRequiredService<TransactionService>().SubmitAsync(userId, tx.Id, "c", default);

        provider.Status = new ProviderStatusReport(ProviderTransferStatus.Successful, null, "999");
        var body = tx.Id.ToString("D");
        CallbackResult first, second;
        using (var scope = sp.CreateScope())
            first = await scope.ServiceProvider.GetRequiredService<TransactionService>().HandleCallbackAsync("mock", new Dictionary<string, string>(), body, "c", default);
        using (var scope = sp.CreateScope())
            second = await scope.ServiceProvider.GetRequiredService<TransactionService>().HandleCallbackAsync("mock", new Dictionary<string, string>(), body, "c", default);

        Assert.Equal(CallbackResult.Applied, first);
        Assert.Equal(CallbackResult.Duplicate, second);
        using var check = sp.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<IKasaDbContext>();
        var saved = await db.Transactions.Include(t => t.Events).SingleAsync(t => t.Id == tx.Id);
        Assert.Equal(TransactionState.Success, saved.State);
        Assert.Equal(1, saved.Events.Count(e => e.To == TransactionState.Success));
        Assert.Equal(1, await db.Notifications.CountAsync(n => n.TransactionId == tx.Id));
    }

    [Fact]
    public async Task A_timed_out_transfer_is_resolved_by_asking_the_provider_never_by_resubmitting()
    {
        var provider = new ScriptedProvider { Outcome = SubmissionOutcome.Uncertain };
        var sp = fixture.Build(s => s.ReplaceProvider(provider));
        var userId = await DemoUserIdAsync(sp);
        var tx = await AuthorizedTransferAsync(sp, userId);
        using (var scope = sp.CreateScope())
        {
            var submitted = await scope.ServiceProvider.GetRequiredService<TransactionService>().SubmitAsync(userId, tx.Id, "c", default);
            Assert.Equal(TransactionState.Unknown, submitted.State);
        }

        provider.Status = new ProviderStatusReport(ProviderTransferStatus.Successful, null, "777");
        using (var scope = sp.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ReconciliationService>().ReconcileAsync(tx.Id, default);

        using var check = sp.CreateScope();
        var saved = await check.ServiceProvider.GetRequiredService<IKasaDbContext>().Transactions.SingleAsync(t => t.Id == tx.Id);
        Assert.Equal(TransactionState.Success, saved.State);
        Assert.Equal(1, provider.Submissions);
        Assert.True(await check.ServiceProvider.GetRequiredService<IKasaDbContext>().ReconciliationRecords.AnyAsync(r => r.TransactionId == tx.Id && r.Resolution == "RESOLVED_BY_PROVIDER_STATUS"));
    }

    [Fact]
    public async Task A_challenge_for_one_transfer_cannot_authenticate_another_and_cannot_be_replayed()
    {
        var sp = fixture.Build();
        var userId = await DemoUserIdAsync(sp);
        using var scope = sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<TransactionService>();
        async Task<Transaction> Confirmed(long minor)
        {
            var t = await svc.CreateAsync(new CreateTransfer(userId, $"b-{Guid.NewGuid():N}", Money.FromMinor(minor, "GHS"), "Ama Owusu",
                Msisdn.Parse("0240000111"), null, "en", null), "c", default);
            return await svc.ConfirmAsync(userId, t.Id, "c", default);
        }
        var small = await Confirmed(5000);
        var large = await Confirmed(90000);
        var smallChallenge = await svc.IssueChallengeAsync(userId, small.Id, null, "c", default);
        await svc.IssueChallengeAsync(userId, large.Id, null, "c", default);

        // The GHS 50 challenge presented for the GHS 900 transfer.
        var cross = await Assert.ThrowsAsync<KasaException>(() => svc.AuthenticateAsync(userId, large.Id,
            new StepUpProof(smallChallenge.ChallengeId, AuthenticationMethod.Passcode, DatabaseFixture.DemoPasscode, null, null), "c", default));
        Assert.Equal("CHALLENGE_NOT_FOUND", cross.Code);

        await svc.AuthenticateAsync(userId, small.Id, new StepUpProof(smallChallenge.ChallengeId, AuthenticationMethod.Passcode, DatabaseFixture.DemoPasscode, null, null), "c", default);
        var attempt = await scope.ServiceProvider.GetRequiredService<IKasaDbContext>().AuthenticationAttempts
            .Where(a => a.TransactionId == small.Id && a.Result == AuthenticationResult.Success).SingleAsync();
        Assert.Equal(5000, attempt.AmountMinor);
        Assert.Equal("024XXX0111", attempt.RecipientMasked);
        Assert.Equal(smallChallenge.ChallengeId, attempt.ChallengeId);

        // Replaying the same, already-used proof achieves nothing.
        var replay = await Assert.ThrowsAsync<KasaException>(() => svc.AuthenticateAsync(userId, small.Id,
            new StepUpProof(smallChallenge.ChallengeId, AuthenticationMethod.Passcode, DatabaseFixture.DemoPasscode, null, null), "c", default));
        Assert.Equal("INVALID_TRANSACTION_TRANSITION", replay.Code);
    }

    [Fact]
    public async Task Another_users_transaction_is_indistinguishable_from_one_that_does_not_exist()
    {
        var sp = fixture.Build();
        var userId = await DemoUserIdAsync(sp);
        var tx = await AuthorizedTransferAsync(sp, userId);
        using var scope = sp.CreateScope();
        var ex = await Assert.ThrowsAsync<KasaException>(() => scope.ServiceProvider.GetRequiredService<TransactionService>().GetAsync(Guid.NewGuid(), tx.Id, "c", default));
        Assert.Equal(ErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task Wrong_passcodes_lock_step_up_and_nothing_is_sent()
    {
        var sp = fixture.Build();
        using var scope = sp.CreateScope();
        var userId = await DemoUserIdAsync(sp);
        var svc = scope.ServiceProvider.GetRequiredService<TransactionService>();
        var t = await svc.CreateAsync(new CreateTransfer(userId, $"l-{Guid.NewGuid():N}", Money.Ghs(10), "Ama Owusu", Msisdn.Parse("0240000111"), null, "en", null), "c", default);
        await svc.ConfirmAsync(userId, t.Id, "c", default);
        var ch = await svc.IssueChallengeAsync(userId, t.Id, null, "c", default);
        var first = await Assert.ThrowsAsync<KasaException>(() => svc.AuthenticateAsync(userId, t.Id, new StepUpProof(ch.ChallengeId, AuthenticationMethod.Passcode, "000001", null, null), "c", default));
        Assert.Equal(ErrorCodes.AuthenticationFailed, first.Code);
        Assert.Equal(2, first.Data["remainingAttempts"]);

        // Reset lockout state for other tests.
        var user = await scope.ServiceProvider.GetRequiredService<IKasaDbContext>().Users.SingleAsync(u => u.Id == userId);
        user.ClearLockout();
        await scope.ServiceProvider.GetRequiredService<IKasaDbContext>().SaveChangesAsync();
        var saved = await svc.GetAsync(userId, t.Id, "c", default);
        Assert.True(TransactionStateMachine.IsBeforeSubmission(saved.State));
    }

    [Fact]
    public async Task Invalid_recipient_fails_validation_and_is_never_submitted()
    {
        var sp = fixture.Build();
        var userId = await DemoUserIdAsync(sp);
        using var scope = sp.CreateScope();
        var t = await scope.ServiceProvider.GetRequiredService<TransactionService>().CreateAsync(new CreateTransfer(userId, $"v-{Guid.NewGuid():N}",
            Money.Ghs(10), "Yaw Darko", Msisdn.Parse("0200000666"), null, "en", null), "c", default);
        Assert.Equal(TransactionState.Failed, t.State);
        Assert.Equal("RECIPIENT_NOT_ACTIVE", t.FailureReason);
        Assert.Null(t.ProviderReference);
    }
}

[Collection("database")]
public class ConversationFlowTests(DatabaseFixture fixture)
{
    private static async Task<(IServiceScope Scope, ConversationService Svc, Guid UserId)> StartAsync(IServiceProvider sp)
    {
        var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IKasaDbContext>();
        var userId = await db.Users.Where(u => u.IsDemo).Select(u => u.Id).SingleAsync();
        return (scope, scope.ServiceProvider.GetRequiredService<ConversationService>(), userId);
    }

    private static TurnInput Say(Guid conversationId, string text, string language = "en") =>
        new(conversationId, Guid.NewGuid().ToString("N"), language, text, null, "text", null);

    [Fact]
    public async Task Send_50_cedis_to_Ama_end_to_end_in_Twi()
    {
        var (scope, svc, userId) = await StartAsync(fixture.Build());
        using var _ = scope;
        var welcome = await svc.WelcomeAsync(userId, "tw", "c", default);

        var heard = await svc.HandleTurnAsync(userId, null, Say(welcome.ConversationId, "Fahana si de aduonum ma Ama.", "tw"), "c", default);
        Assert.Equal(ConversationStage.AwaitingConfirmation, heard.Stage);
        Assert.Equal(5000, heard.Transaction!.AmountMinor);
        Assert.Contains("Ghana sidi aduonum", heard.Speech!.Text);
        Assert.Equal("server", heard.Speech.Engine); // Twi is spoken by Khaya, not the phone

        var confirmed = await svc.HandleTurnAsync(userId, null, Say(welcome.ConversationId, "Aane", "tw"), "c", default);
        Assert.Equal(ConversationStage.AwaitingAuthentication, confirmed.Stage);
        Assert.NotNull(confirmed.Authentication);

        var sent = await svc.AuthenticateAsync(userId, new AuthenticateInput(welcome.ConversationId, confirmed.Authentication!.ChallengeId,
            AuthenticationMethod.Passcode, DatabaseFixture.DemoPasscode, null, null), "c", default);
        Assert.Equal(ConversationStage.Processing, sent.Stage);
        Assert.Equal(TransactionState.Submitted, sent.Transaction!.State);
        Assert.DoesNotContain("akɔ yie", sent.Speech!.Text); // accepted is not success

        ConversationReply result = sent;
        for (var i = 0; i < 20 && result.Stage == ConversationStage.Processing; i++)
        {
            await Task.Delay(500);
            result = await svc.CheckStatusAsync(userId, welcome.ConversationId, "c", default);
        }
        Assert.Equal(TransactionState.Success, result.Transaction!.State);
        Assert.Contains("akɔ yie", result.Speech!.Text);
    }

    [Fact]
    public async Task Ambiguous_name_asks_which_person_and_speech_cannot_skip_confirmation()
    {
        var (scope, svc, userId) = await StartAsync(fixture.Build());
        using var _ = scope;
        var c = (await svc.WelcomeAsync(userId, "en", "c", default)).ConversationId;

        var ask = await svc.HandleTurnAsync(userId, null, Say(c, "Send 20 cedis to Kofi"), "c", default);
        Assert.Equal(ConversationStage.ChoosingRecipient, ask.Stage);
        Assert.Null(ask.Transaction);
        Assert.Equal(2, ask.Candidates!.Count);

        var chosen = await svc.HandleTurnAsync(userId, null, Say(c, "the second one"), "c", default);
        Assert.Equal(ConversationStage.AwaitingConfirmation, chosen.Stage);
        Assert.Equal(ask.Candidates[1].Name, chosen.Transaction!.Recipient.DisplayName); // exactly the one read out as "2"
        Assert.Equal(["Kofi Boateng", "Kofi Mensah"], ask.Candidates.Select(x => x.Name)); // stable, alphabetical
        Assert.Equal(TransactionState.ReadyForConfirmation, chosen.Transaction.State);
    }

    [Fact]
    public async Task A_repeated_voice_turn_is_processed_once()
    {
        var (scope, svc, userId) = await StartAsync(fixture.Build());
        using var _ = scope;
        var c = (await svc.WelcomeAsync(userId, "en", "c", default)).ConversationId;
        var turn = Say(c, "Send 30 cedis to Ama");
        var first = await svc.HandleTurnAsync(userId, null, turn, "c", default);
        var replay = await svc.HandleTurnAsync(userId, null, turn, "c", default);
        Assert.Equal(first.Transaction!.Id, replay.Transaction!.Id);
    }
}
