using KasaSika.Application.Language;

namespace KasaSika.UnitTests.Language;

public class ResponseCatalogTests
{
    private static readonly TimeProvider Clock = TimeProvider.System;

    [Fact]
    public void Every_reply_exists_in_English_and_Twi() => Assert.True(ResponseCatalog.HasLanguage("en") && ResponseCatalog.HasLanguage("tw"));

    [Theory]
    [InlineData(5000, "en", "50 Ghana cedis")]
    [InlineData(5020, "en", "50 Ghana cedis and 20 pesewas")]
    [InlineData(5000, "tw", "Ghana sidi aduonum")]
    [InlineData(15000, "tw", "Ghana sidi ɔha ne aduonum")]
    [InlineData(250000, "tw", "Ghana sidi mpem mmienu ne ahanum")]
    [InlineData(2100, "tw", "Ghana sidi aduonu baako")]
    public void Amounts_are_spoken_as_words_in_each_language(long minor, string language, string expected) =>
        Assert.Equal(expected, ResponseCatalog.Amount(minor, language));

    [Fact]
    public void Confirmation_reads_back_amount_recipient_and_last_four_digits()
    {
        var text = ResponseCatalog.Render(ReplyPart.Of("CONFIRM_SEND", new ReplyArgs { AmountMinor = 5000, Name = "Ama Owusu", LastFour = "0111" }), "en", Clock);
        Assert.Equal("You are about to send 50 Ghana cedis to Ama Owusu, number ending 0 1 1 1. Say yes to continue, or no to cancel.", text);
    }

    [Fact]
    public void Uncertain_outcomes_are_never_described_as_success_or_failure()
    {
        var text = ResponseCatalog.Render(ReplyPart.Of("UNKNOWN"), "en", Clock);
        Assert.Contains("not marked it as successful or failed", text);
        Assert.DoesNotContain("was successful", text);
    }

    [Fact]
    public void Every_template_renders_without_leftover_placeholders()
    {
        var args = new ReplyArgs
        {
            Name = "Ama", AmountMinor = 5000, LimitMinor = 100000, LastFour = "0111", Count = 2, Date = DateTimeOffset.UtcNow,
            Status = "Success", Options = [new ReplyOption("Kofi Mensah", "0333"), new ReplyOption("Kofi Boateng", "0444")],
        };
        foreach (var key in ResponseCatalog.Keys)
            foreach (var lang in new[] { "en", "tw" })
                Assert.DoesNotContain("{", ResponseCatalog.Render(ReplyPart.Of(key, args), lang, Clock));
    }
}
