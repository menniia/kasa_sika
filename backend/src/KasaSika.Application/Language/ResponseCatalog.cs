using System.Globalization;
using System.Text;
using KasaSika.Domain;

namespace KasaSika.Application.Language;

/// <summary>Typed arguments for a reply, so every language formats amounts, digits and dates its own way.</summary>
public sealed record ReplyArgs
{
    public string? Name { get; init; }
    public long? AmountMinor { get; init; }
    public long? LimitMinor { get; init; }
    public string? LastFour { get; init; }
    public int? Count { get; init; }
    public DateTimeOffset? Date { get; init; }
    /// <summary>A transaction state name (Success, Pending, ...), spoken as a plain-language status.</summary>
    public string? Status { get; init; }
    public List<ReplyOption>? Options { get; init; }
    public static readonly ReplyArgs None = new();
}

public sealed record ReplyOption(string Name, string LastFour);

/// <summary>One sentence (or a few) from the catalogue. A reply is a list of parts.</summary>
public sealed record ReplyPart(string Key, ReplyArgs Args)
{
    public static ReplyPart Of(string key, ReplyArgs? args = null) => new(key, args ?? ReplyArgs.None);
}

/// <summary>
/// Everything Kasa Sika says, in every supported language.
///
/// Twi lines are CURATED TEMPLATES, not runtime machine translation: in the live test, Khaya translation of a
/// confirmation prompt dropped "to stop", and a financial confirmation cannot lose meaning. The Twi below was
/// drafted for this prototype and REQUIRES NATIVE-SPEAKER REVIEW before judging or production (see
/// docs/integrations/khaya.md, "Twi review checklist"). Money terms stay recognisable to Ghanaian users:
/// "Ghana sidi", "MoMo", "MTN".
///
/// Adding Ewe/Ga/Dagbani = add a column here and enable the language in configuration.
/// </summary>
public static class ResponseCatalog
{
    private static readonly Dictionary<string, Dictionary<string, string>> Templates = new()
    {
        ["MODE_MOCK"] = new()
        {
            ["en"] = "This is a demonstration. No real money moves.",
            ["tw"] = "Yei yɛ nhwɛsoɔ. Sika ankasa biara nkɔ.",
        },
        ["MODE_SANDBOX"] = new()
        {
            ["en"] = "This is the MTN test environment. No real money moves.",
            ["tw"] = "Yei yɛ MTN sɔhwɛ beaeɛ. Sika ankasa biara nkɔ.",
        },
        ["WELCOME"] = new()
        {
            ["en"] = "Welcome to Kasa Sika, {name}. Tell me what you want to do. For example, say: send 50 cedis to Ama.",
            ["tw"] = "Akwaaba, {name}. Yei ne Kasa Sika. Ka deɛ wopɛ sɛ woyɛ. Sɛ nhwɛsoɔ no, ka sɛ: fa Ghana sidi aduonum kɔma Ama.",
        },
        ["ASK_WHAT"] = new()
        {
            ["en"] = "What would you like to do? You can say: send 50 cedis to Ama.",
            ["tw"] = "Ɛdeɛn na wopɛ sɛ woyɛ? Wobɛtumi aka sɛ: fa Ghana sidi aduonum kɔma Ama.",
        },
        ["ASK_AMOUNT"] = new()
        {
            ["en"] = "How much do you want to send to {name}?",
            ["tw"] = "Sika dodoɔ bɛn na wopɛ sɛ wode kɔma {name}?",
        },
        ["ASK_RECIPIENT"] = new()
        {
            ["en"] = "Who do you want to send {amount} to?",
            ["tw"] = "Hwan na wopɛ sɛ wode {amount} kɔma no?",
        },
        ["CHOOSE_RECIPIENT"] = new()
        {
            ["en"] = "I found {count} people called {name}. {options}. Which one? Say one or two.",
            ["tw"] = "Mehunuu nnipa {count} a wɔfrɛ wɔn {name}. {options}. Ɛhe na wopɛ? Ka baako anaa mmienu.",
        },
        ["CHOICE_NOT_UNDERSTOOD"] = new()
        {
            ["en"] = "Sorry, which one? {options}.",
            ["tw"] = "Kafra, ɛhe na wopɛ? {options}.",
        },
        ["RECIPIENT_UNKNOWN"] = new()
        {
            ["en"] = "I could not find {name} in your contacts. Please say the name again, or say their phone number.",
            ["tw"] = "Manhunu {name} wɔ wo contacts mu. Yɛsrɛ wo, ka ne din bio, anaa ka ne phone nɔma.",
        },
        ["CONFIRM_SEND"] = new()
        {
            ["en"] = "You are about to send {amount} to {name}, number ending {lastFour}. Say yes to continue, or no to cancel.",
            ["tw"] = "Worebɛfa {amount} akɔma {name}, ne nɔma a ɛwie {lastFour}. Sɛ ɛyɛ nokware a, ka aane. Sɛ ɛnte saa a, ka daabi.",
        },
        ["CONFIRM_EXPECTED"] = new()
        {
            ["en"] = "Please say yes to send, or no to cancel. You can also say repeat.",
            ["tw"] = "Yɛsrɛ wo, ka aane na yɛasoma, anaa daabi na yɛagyae. Wobɛtumi nso aka san ka.",
        },
        ["RECIPIENT_INACTIVE"] = new()
        {
            ["en"] = "{name}'s number is not an active mobile money account, so I have not sent anything.",
            ["tw"] = "{name} nɔma no nni mobile money account a ɛyɛ adwuma, enti mensomaa hwee.",
        },
        ["AUTH_REQUIRED"] = new()
        {
            ["en"] = "Please confirm it is you. Use your fingerprint, or type your Kasa Sika passcode on the keypad. Do not say your passcode out loud.",
            ["tw"] = "Yɛsrɛ wo, kyerɛ sɛ wo ara ne no. Fa wo nsateaa, anaa hyɛ wo Kasa Sika passcode no wɔ keypad no so. Nka wo passcode no den.",
        },
        ["AUTH_WAITING"] = new()
        {
            ["en"] = "I am waiting for you to confirm it is you, with your fingerprint or passcode. Say cancel to stop.",
            ["tw"] = "Meretwɛn sɛ wobɛkyerɛ sɛ wo ara ne no, wo nsateaa anaa wo passcode. Ka gyae sɛ wopɛ sɛ wogyae.",
        },
        ["AUTH_FAILED"] = new()
        {
            ["en"] = "That passcode was not correct. You have {count} tries left. Nothing has been sent.",
            ["tw"] = "Passcode no nteɛ. Wowɔ kwan {count} aka. Mensomaa hwee.",
        },
        ["AUTH_LOCKED"] = new()
        {
            ["en"] = "Too many wrong attempts. For your safety, transfers are paused for a while. Nothing has been sent.",
            ["tw"] = "Woayɛ mfomsoɔ bebree dodo. Sɛ ɛbɛyɛ a wo sika bɛdi mu nti, yɛagyina sika soma so kakra. Mensomaa hwee.",
        },
        ["PROCESSING"] = new()
        {
            ["en"] = "Thank you. You are verified. I am sending your request to MTN now.",
            ["tw"] = "Meda wo ase. Yɛagye wo atom. Merefa w'abisadeɛ akɔma MTN seesei.",
        },
        ["PENDING"] = new()
        {
            ["en"] = "Your transaction is being processed. I will tell you as soon as MTN confirms it.",
            ["tw"] = "Wo sika soma no rekɔ so. Sɛ MTN si so dua a, mɛka akyerɛ wo ntɛm ara.",
        },
        ["SUCCESS"] = new()
        {
            ["en"] = "Your {amount} transfer to {name} was successful.",
            ["tw"] = "Wo {amount} a wode kɔmaa {name} no akɔ yie.",
        },
        ["FAILED"] = new()
        {
            ["en"] = "MTN reported that your transfer to {name} did not go through. The money was not sent.",
            ["tw"] = "MTN aka sɛ wo sika soma a ɛkɔ {name} hɔ no annyɛ yie. Sika no ankɔ.",
        },
        ["FAILED_NO_RECORD"] = new()
        {
            ["en"] = "MTN has no record of your transfer to {name}, so no money was sent.",
            ["tw"] = "MTN nni wo sika soma a ɛkɔ {name} hɔ no ho kyerɛwtohɔ biara, enti sika biara ankɔ.",
        },
        ["UNKNOWN"] = new()
        {
            ["en"] = "I cannot confirm the result of this transaction yet. I have not marked it as successful or failed. I will keep checking with MTN.",
            ["tw"] = "Mentumi nsi sika soma yi so dua seesei. Menkaa sɛ ɛkɔɔ yie anaa ɛannyɛ yie. Mɛkɔ so abisa MTN.",
        },
        ["CANCELLED"] = new()
        {
            ["en"] = "Okay, I have cancelled it. No money was sent.",
            ["tw"] = "Yoo, magyae. Sika biara ankɔ.",
        },
        ["CANNOT_CANCEL"] = new()
        {
            ["en"] = "This transfer has already been sent to MTN, so it can no longer be cancelled. I will tell you the result.",
            ["tw"] = "Yɛde sika soma yi akɔma MTN dada, enti yɛrentumi nnyae bio. Mɛka nea efiri mu ba akyerɛ wo.",
        },
        ["NOTHING_TO_CANCEL"] = new()
        {
            ["en"] = "There is nothing to cancel.",
            ["tw"] = "Biribiara nni hɔ a ɛsɛ sɛ yɛgyae.",
        },
        ["EXPIRED"] = new()
        {
            ["en"] = "That request took too long, so I stopped it. Nothing was sent. Please start again.",
            ["tw"] = "Abisadeɛ no dii bere pii, enti magyae. Mensomaa hwee. Yɛsrɛ wo, fi ase bio.",
        },
        ["NOT_UNDERSTOOD"] = new()
        {
            ["en"] = "Sorry, I did not understand that. Please say it again. For example: send 50 cedis to Ama.",
            ["tw"] = "Kafra, mante aseɛ. Yɛsrɛ wo, ka bio. Sɛ nhwɛsoɔ no: fa sidi aduonum kɔma Ama.",
        },
        ["NO_SPEECH"] = new()
        {
            ["en"] = "I did not hear anything. Please try again.",
            ["tw"] = "Mante hwee. Yɛsrɛ wo, ka bio.",
        },
        ["AMOUNT_TOO_HIGH"] = new()
        {
            ["en"] = "{amount} is more than the limit of {limit} for one transfer. Please say a smaller amount.",
            ["tw"] = "{amount} boro {limit} a wobɛtumi de asoma bere koro. Yɛsrɛ wo, ka sika kakraa bi.",
        },
        ["DAILY_LIMIT"] = new()
        {
            ["en"] = "This would go over your daily limit, so I have not sent it.",
            ["tw"] = "Yei bɛboro wo da biara sika soma ano, enti mensomaa.",
        },
        ["PROVIDER_UNAVAILABLE"] = new()
        {
            ["en"] = "I couldn't reach the payment service. I have not sent your transaction. Please try again in a moment.",
            ["tw"] = "Mentumi nnu payment service no so. Mensomaa wo sika no. Yɛsrɛ wo, sɔ hwɛ bio kakra akyi.",
        },
        ["SPEECH_UNAVAILABLE"] = new()
        {
            ["en"] = "The voice service is not available right now. Nothing has been sent. You can type your request instead.",
            ["tw"] = "Nne ho adwuma no nni hɔ seesei. Mensomaa hwee. Wobɛtumi atwerɛ w'abisadeɛ.",
        },
        ["LAST_TX"] = new()
        {
            ["en"] = "Your last transaction was {amount} to {name}, {date}. Status: {status}.",
            ["tw"] = "Wo sika soma a ɛtwa toɔ no yɛ {amount} kɔmaa {name}, {date}. Ne tebea: {status}.",
        },
        ["NO_TX"] = new()
        {
            ["en"] = "You have not made any transactions yet.",
            ["tw"] = "Wonsomaa sika biara ɛ.",
        },
        ["TODAY_TOTAL"] = new()
        {
            ["en"] = "Today you have sent {amount}, in {count} successful transfers.",
            ["tw"] = "Ɛnnɛ, wode {amount} asoma, sika soma {count} a ɛkɔɔ yie.",
        },
        ["TODAY_NONE"] = new()
        {
            ["en"] = "You have not sent any money today.",
            ["tw"] = "Ɛnnɛ, wonsomaa sika biara.",
        },
        ["BALANCE_UNAVAILABLE"] = new()
        {
            ["en"] = "I cannot check your MoMo balance yet. That needs your consent through MTN, which this prototype does not have. Please check it in the MTN MoMo menu.",
            ["tw"] = "Mentumi nhwɛ wo MoMo sika dodoɔ seesei. Ɛhia sɛ wopene so denam MTN so, na prototype yi nni saa kwan no. Yɛsrɛ wo, hwɛ wɔ MTN MoMo menu no mu.",
        },
        ["HELP_IDLE"] = new()
        {
            ["en"] = "You can say: send 50 cedis to Ama. What was my last transaction? How much did I send today? At any time you can say repeat, cancel, go back, or start again.",
            ["tw"] = "Wobɛtumi aka sɛ: fa sidi aduonum kɔma Ama. Wobɛtumi abisa wo sika soma a ɛtwa toɔ, anaa sika a wode asoma ɛnnɛ. Bere biara wobɛtumi aka: san ka, gyae, san w'akyi, anaa fi ase bio.",
        },
        ["HELP_CONFIRM"] = new()
        {
            ["en"] = "Say yes to send the money, or no to cancel. To change the amount, say for example: send 20 cedis instead.",
            ["tw"] = "Ka aane na yɛasoma sika no, anaa daabi na yɛagyae. Sɛ wopɛ sɛ wosesa sika dodoɔ no a, ka sɛ: fa sidi aduonu mmom.",
        },
        ["HELP_AUTH"] = new()
        {
            ["en"] = "Touch the fingerprint sensor, or type your six digit Kasa Sika passcode on the keypad. This is not your MoMo PIN. Say cancel to stop.",
            ["tw"] = "Fa wo nsateaa ka sensor no, anaa hyɛ wo Kasa Sika passcode nɔma nsia no wɔ keypad no so. Ɛnyɛ wo MoMo PIN. Ka gyae sɛ wopɛ sɛ wogyae.",
        },
        ["HELP_CHOOSE"] = new()
        {
            ["en"] = "Say one or two to choose the person, or say their full name.",
            ["tw"] = "Ka baako anaa mmienu na yi onipa no, anaa ka ne din nyinaa.",
        },
        ["BACK_TO_FLOW"] = new()
        {
            ["en"] = "Now, back to your transfer.",
            ["tw"] = "Seesei, yɛsan kɔ wo sika soma no so.",
        },
        ["NOTHING_TO_GO_BACK"] = new()
        {
            ["en"] = "We are already at the beginning.",
            ["tw"] = "Yɛwɔ mfiaseɛ dada.",
        },
        ["STARTED_AGAIN"] = new()
        {
            ["en"] = "Okay, let us start again. Nothing was sent.",
            ["tw"] = "Yoo, momma yɛnfi ase bio. Mensomaa hwee.",
        },
        ["CHANGED"] = new()
        {
            ["en"] = "Okay, I have changed it.",
            ["tw"] = "Yoo, masesa.",
        },
        ["STATUS_NOW"] = new()
        {
            ["en"] = "Your transfer of {amount} to {name}: {status}.",
            ["tw"] = "Wo {amount} a wode kɔma {name} no: {status}.",
        },
        ["TX_ACTIVE"] = new()
        {
            ["en"] = "Your transfer to {name} is still being processed. I will tell you when MTN confirms it.",
            ["tw"] = "Wo sika soma a ɛkɔ {name} hɔ no da so rekɔ so. Sɛ MTN si so dua a, mɛka akyerɛ wo.",
        },
        ["OFFLINE"] = new()
        {
            ["en"] = "You are offline. I have not sent the transaction.",
            ["tw"] = "Wonni intanɛt so. Mensomaa sika no.",
        },
    };

    /// <summary>What the phone says, in English, if the Twi voice cannot be played. Honest, never Twi text in an English voice.</summary>
    public const string TwiVoiceUnavailableEnglish = "The Twi voice is not available right now, so I will speak English. ";

    private static readonly Dictionary<string, Dictionary<string, string>> StatusWords = new()
    {
        ["en"] = new()
        {
            ["Success"] = "successful", ["Failed"] = "failed", ["Cancelled"] = "cancelled", ["Expired"] = "expired, nothing was sent",
            ["Unknown"] = "status unknown, still checking with MTN", ["ReconciliationRequired"] = "status unknown, being checked with MTN",
            ["Reversed"] = "reversed", ["Pending"] = "processing", ["Submitted"] = "processing", ["Submitting"] = "processing",
        },
        ["tw"] = new()
        {
            ["Success"] = "ɛkɔɔ yie", ["Failed"] = "ɛannyɛ yie", ["Cancelled"] = "wɔgyaeɛ", ["Expired"] = "ne bere twaeɛ, ɛankɔ",
            ["Unknown"] = "yɛnnim ne tebea seesei, yɛda so rebisa MTN", ["ReconciliationRequired"] = "yɛnnim ne tebea seesei, yɛrehwɛ mu wɔ MTN",
            ["Reversed"] = "wɔsan de aba", ["Pending"] = "ɛrekɔ so", ["Submitted"] = "ɛrekɔ so", ["Submitting"] = "ɛrekɔ so",
        },
    };

    public static IEnumerable<string> Keys => Templates.Keys;

    public static bool HasLanguage(string language) => Templates.Values.All(t => t.ContainsKey(language));

    public static string Render(IEnumerable<ReplyPart> parts, string language, TimeProvider clock) =>
        string.Join(" ", parts.Select(p => Render(p, language, clock)));

    public static string Render(ReplyPart part, string language, TimeProvider clock)
    {
        if (!Templates.TryGetValue(part.Key, out var byLanguage))
            throw new InvalidOperationException($"No reply template '{part.Key}'.");
        var template = byLanguage.TryGetValue(language, out var t) ? t : byLanguage["en"];
        var lang = byLanguage.ContainsKey(language) ? language : "en";
        var a = part.Args;
        var sb = new StringBuilder(template);
        sb.Replace("{name}", a.Name ?? "");
        sb.Replace("{amount}", a.AmountMinor is { } amount ? Amount(amount, lang) : "");
        sb.Replace("{limit}", a.LimitMinor is { } limit ? Amount(limit, lang) : "");
        sb.Replace("{lastFour}", a.LastFour is { } lf ? Digits(lf, lang) : "");
        sb.Replace("{count}", a.Count is { } c ? Count(c, lang) : "");
        sb.Replace("{date}", a.Date is { } d ? Date(d, lang, clock) : "");
        sb.Replace("{status}", a.Status is { } s ? Status(s, lang) : "");
        sb.Replace("{options}", a.Options is { } o ? Options(o, lang) : "");
        return sb.ToString();
    }

    /// <summary>"50 Ghana cedis" / "Ghana sidi aduonum". Integer minor units in, words out: no floating point anywhere.</summary>
    public static string Amount(long minor, string language)
    {
        var whole = minor / 100;
        var pesewas = minor % 100;
        return language switch
        {
            "tw" => pesewas == 0
                ? $"Ghana sidi {TwiWords.Number(whole)}"
                : $"Ghana sidi {TwiWords.Number(whole)} ne pesewa {TwiWords.Number(pesewas)}",
            _ => pesewas == 0
                ? $"{whole.ToString("N0", CultureInfo.InvariantCulture)} Ghana cedis"
                : $"{whole.ToString("N0", CultureInfo.InvariantCulture)} Ghana cedis and {pesewas} pesewas",
        };
    }

    public static string Status(string state, string language) =>
        StatusWords.TryGetValue(language, out var words) && words.TryGetValue(state, out var w) ? w
        : StatusWords["en"].TryGetValue(state, out var en) ? en : "in progress";

    private static string Digits(string digits, string language) =>
        language == "tw" ? TwiWords.DigitByDigit(digits) : string.Join(" ", digits.ToCharArray());

    private static string Count(int count, string language) =>
        language == "tw" ? TwiWords.Number(count) : count.ToString(CultureInfo.InvariantCulture);

    private static string Date(DateTimeOffset when, string language, TimeProvider clock)
    {
        // Ghana is on GMT all year, so UTC dates are Ghana dates.
        var today = clock.GetUtcNow().UtcDateTime.Date;
        var day = when.UtcDateTime.Date;
        if (language == "tw")
        {
            if (day == today) return "ɛnnɛ";
            if (day == today.AddDays(-1)) return "ɛnnora";
            return $"{TwiWords.Month(day.Month)} da {TwiWords.Number(day.Day)}";
        }
        if (day == today) return "today";
        if (day == today.AddDays(-1)) return "yesterday";
        return "on " + day.ToString("d MMMM", CultureInfo.InvariantCulture);
    }

    private static string Options(List<ReplyOption> options, string language) =>
        string.Join(". ", options.Select((o, i) => language == "tw"
            ? $"{TwiWords.Number(i + 1)}: {o.Name}, nɔma a ɛwie {TwiWords.DigitByDigit(o.LastFour)}"
            : $"{i + 1}: {o.Name}, number ending {string.Join(" ", o.LastFour.ToCharArray())}"));
}
