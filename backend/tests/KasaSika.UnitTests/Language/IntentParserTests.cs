using KasaSika.Application.Language;
using KasaSika.Domain;
using KasaSika.Domain.Contacts;

namespace KasaSika.UnitTests.Language;

public class IntentParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.NewGuid();

    private static readonly List<Contact> Contacts =
    [
        Contact.Create(UserId, "Ama Owusu", Msisdn.Parse("0240000111"), ["Maame Ama"], Now),
        Contact.Create(UserId, "Kwame Asante", Msisdn.Parse("0240000222"), [], Now),
        Contact.Create(UserId, "Kofi Mensah", Msisdn.Parse("0240000333"), [], Now),
        Contact.Create(UserId, "Kofi Boateng", Msisdn.Parse("0240000444"), [], Now),
    ];

    private static ParsedUtterance Parse(string text) => IntentParser.Parse(text, Contacts);

    [Theory]
    [InlineData("Send 50 cedis to Ama", 5000)]
    [InlineData("send fifty cedis to Ama.", 5000)]
    [InlineData("Please send GH₵50 to Ama", 5000)]
    [InlineData("I want to send one hundred and fifty cedis to Ama", 15000)]
    [InlineData("send 12.50 cedis to ama", 1250)]
    [InlineData("Send 50 cedis 20 pesewas to Ama", 5020)]
    [InlineData("pay ama 5 hundred", 50000)]
    public void English_send_requests_give_amount_in_minor_units_and_the_contact(string text, long minor)
    {
        var p = Parse(text);
        Assert.True(p.LooksLikeSend);
        Assert.Equal(minor, p.AmountMinor);
        Assert.Single(p.RecipientMatches);
        Assert.Equal("Ama Owusu", p.RecipientMatches[0].Contact.DisplayName);
    }

    [Theory]
    // The real Khaya ASR output for the spoken Twi request "Fa Ghana sidi aduonum kɔma Ama" (2026-09-22).
    [InlineData("Fahana si de aduonum ma Ama.", 5000)]
    [InlineData("Fa Ghana sidi aduonum kɔma Ama", 5000)]
    [InlineData("Soma sika ɔha ne aduonum kɔma Ama", 15000)]
    [InlineData("fa sidi aduonu baako ma Ama", 2100)]
    // Code-switching: English numbers in a Twi sentence and vice versa.
    [InlineData("Fa 50 cedis kɔma Ama", 5000)]
    [InlineData("Send aduonum cedis to Ama", 5000)]
    public void Twi_and_code_switched_requests_are_understood(string text, long minor)
    {
        var p = Parse(text);
        Assert.Equal(minor, p.AmountMinor);
        Assert.Single(p.RecipientMatches);
        Assert.Equal("Ama Owusu", p.RecipientMatches[0].Contact.DisplayName);
    }

    [Fact]
    public void Two_people_with_the_same_first_name_are_both_returned_never_one_guessed()
    {
        var p = Parse("Send 20 cedis to Kofi");
        Assert.Equal(2, p.RecipientMatches.Count);
    }

    [Fact]
    public void A_full_name_resolves_the_ambiguity()
    {
        var p = Parse("Send 20 cedis to Kofi Boateng");
        Assert.Single(p.RecipientMatches);
        Assert.Equal("Kofi Boateng", p.RecipientMatches[0].Contact.DisplayName);
    }

    [Fact]
    public void A_one_letter_recognition_slip_in_a_long_name_still_matches()
    {
        var p = Parse("send 10 cedis to Kwami");
        Assert.Equal("Kwame Asante", Assert.Single(p.RecipientMatches).Contact.DisplayName);
    }

    [Fact]
    public void Missing_amount_is_reported_as_missing_not_guessed()
    {
        var p = Parse("Send money to Kwame");
        Assert.Null(p.AmountMinor);
        Assert.True(p.HasSendVerb);
        Assert.Single(p.RecipientMatches);
    }

    [Fact]
    public void Unknown_recipient_name_is_captured_for_the_clarifying_question()
    {
        var p = Parse("Send 30 cedis to Kojo");
        Assert.Empty(p.RecipientMatches);
        Assert.Equal("Kojo", p.UnmatchedRecipientName);
    }

    [Theory]
    [InlineData("send 20 cedis to 0244 123 456", "233244123456")]
    [InlineData("send 20 cedis to zero two four four one two three four five six", "233244123456")]
    public void Phone_numbers_are_recognised_and_not_mistaken_for_the_amount(string text, string international)
    {
        var p = Parse(text);
        Assert.Equal(international, p.PhoneNumber?.International);
        Assert.Equal(2000, p.AmountMinor);
    }

    [Theory]
    [InlineData("yes", Command.Confirm)]
    [InlineData("Aane", Command.Confirm)]
    [InlineData("daabi", Command.Deny)]
    [InlineData("cancel", Command.Cancel)]
    [InlineData("gyae", Command.Cancel)]
    [InlineData("repeat that", Command.Repeat)]
    [InlineData("san ka", Command.Repeat)]
    [InlineData("help", Command.Help)]
    [InlineData("go back", Command.GoBack)]
    [InlineData("start again", Command.StartAgain)]
    [InlineData("what was my last transaction", Command.LastTransaction)]
    [InlineData("how much did I send today", Command.TodayTotal)]
    [InlineData("check my balance", Command.Balance)]
    public void Conversational_controls_are_recognised_in_both_languages(string text, Command expected) =>
        Assert.Contains(expected, Parse(text).Commands);

    [Fact]
    public void Instead_marks_a_correction()
    {
        var p = Parse("send 20 cedis instead");
        Assert.True(p.SaysInstead);
        Assert.Equal(2000, p.AmountMinor);
    }

    [Fact]
    public void Interjection_oh_is_not_an_amount_of_zero()
    {
        Assert.Null(Parse("oh").AmountMinor);
    }

    [Fact]
    public void Overlong_input_is_truncated_not_rejected_with_an_exception()
    {
        var p = Parse(new string('a', 5000));
        Assert.False(p.LooksLikeSend);
    }
}
