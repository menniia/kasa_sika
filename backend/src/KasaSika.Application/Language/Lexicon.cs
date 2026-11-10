namespace KasaSika.Application.Language;

/// <summary>Conversational controls the user can say at any point.</summary>
public enum Command
{
    Confirm,
    Deny,
    Cancel,
    Repeat,
    Help,
    GoBack,
    StartAgain,
    LastTransaction,
    TodayTotal,
    Balance,
    Status,
}

/// <summary>
/// Words and phrases, in folded form (see <see cref="TextNormalizer.Fold"/>), for English, Twi and the
/// code-switched speech people actually use ("Send 50 cedis kɔma Ama"). Every language is always active:
/// a Twi speaker saying "confirm" or an English speaker saying "aane" is understood.
///
/// Twi entries are a starting set and REQUIRE NATIVE-SPEAKER REVIEW; add variants as they are observed from
/// the recogniser rather than guessing more.
/// </summary>
public static class Lexicon
{
    public static readonly IReadOnlyDictionary<Command, string[]> Commands = new Dictionary<Command, string[]>
    {
        [Command.Confirm] =
        [
            "yes", "yeah", "yea", "yep", "yup", "confirm", "confirmed", "correct", "that is right", "thats right", "right",
            "okay", "ok", "sure", "proceed", "continue", "go ahead", "send it", "do it",
            "aane", "aan", "yiw", "yoo", "ampa", "eye", "eye papa", "toa so",
        ],
        [Command.Deny] = ["no", "nope", "not correct", "wrong", "not right", "daabi", "dabi", "enye", "enye saa"],
        [Command.Cancel] = ["cancel", "stop", "never mind", "nevermind", "abort", "forget it", "gyae", "gyae ho", "twa mu"],
        [Command.Repeat] = ["repeat", "say again", "say that again", "again please", "pardon", "what did you say", "come again", "san ka", "ka bio", "kasa bio"],
        [Command.Help] = ["help", "what can i say", "what can you do", "how does this work", "boa me", "mmoa", "boa"],
        [Command.GoBack] = ["go back", "back", "previous", "undo", "san wakyi", "san kyi"],
        [Command.StartAgain] = ["start again", "start over", "restart", "begin again", "from the beginning", "fi ase bio", "hye ase bio", "fi ase"],
        [Command.LastTransaction] =
        [
            "last transaction", "last transfer", "last payment", "my last", "what did i send", "who did i send", "previous transaction",
            "etwa to", "twa to", "nea etwa to",
        ],
        [Command.TodayTotal] = ["today", "how much did i send", "total sent", "sent today", "enne", "nne"],
        [Command.Balance] = ["balance", "how much do i have", "how much money do i have", "sika dodoo", "me sika dodoo", "sika a mewo"],
        [Command.Status] = ["status", "what happened", "is it done", "has it gone", "did it go", "check", "tebea", "aba no"],
    };

    /// <summary>Words that signal a request to move money.</summary>
    public static readonly HashSet<string> SendVerbs =
    [
        "send", "sent", "transfer", "pay", "give", "momo", "remit",
        "fa", "soma", "som", "de", "koma", "kma", "mane",
    ];

    /// <summary>"Send 20 cedis instead": a correction, not a new request.</summary>
    public static readonly HashSet<string> InsteadWords = ["instead", "rather", "change", "mmom", "sesa"];

    /// <summary>Words that introduce the recipient ("to Ama", "kɔma Ama", "ma Ama").</summary>
    public static readonly HashSet<string> RecipientMarkers = ["to", "for", "koma", "ma", "kma"];

    /// <summary>Tokens that can never be part of a person's name.</summary>
    public static readonly HashSet<string> Filler =
    [
        "i", "im", "me", "my", "want", "wanna", "would", "like", "please", "kindly", "can", "could", "you", "to", "for",
        "the", "a", "an", "some", "money", "mobile", "cash", "now", "and", "of", "number", "phone", "is", "it", "that", "this",
        "hi", "hello", "kasa", "sika", "wo", "me", "mepa", "wokyew", "mesre", "yesre", "na", "no", "ne", "so", "ho", "hwe",
        "one", "first", "second", "third", "kan", "ma",
    ];

    public static readonly IReadOnlyDictionary<string, int> Ordinals = new Dictionary<string, int>
    {
        ["first"] = 1, ["1st"] = 1, ["second"] = 2, ["2nd"] = 2, ["third"] = 3, ["3rd"] = 3,
        ["fourth"] = 4, ["fifth"] = 5, ["kan"] = 1, ["dikan"] = 1,
    };
}
