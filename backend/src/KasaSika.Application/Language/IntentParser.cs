using KasaSika.Domain;
using KasaSika.Domain.Contacts;

namespace KasaSika.Application.Language;

/// <summary>A contact that matched part of the utterance. Score 2 = full name / alias, 1 = first or last name only.</summary>
public sealed record RecipientMatch(Contact Contact, int Score);

/// <summary>
/// The facts found in one utterance. It deliberately does NOT decide what the user meant: "no" is a denial
/// while confirming but noise elsewhere; "2" is a choice while choosing between two Kofis but an amount
/// otherwise. <c>ConversationService</c> interprets these facts in the context of the conversation stage.
/// </summary>
public sealed record ParsedUtterance(
    string Normalized,
    IReadOnlySet<Command> Commands,
    bool HasSendVerb,
    bool SaysInstead,
    IReadOnlyList<NumberSpan> Numbers,
    long? AmountMinor,
    Msisdn? PhoneNumber,
    IReadOnlyList<RecipientMatch> RecipientMatches,
    string? UnmatchedRecipientName,
    int? Ordinal)
{
    public bool IsEmpty => Normalized.Length == 0;
    public bool Has(Command command) => Commands.Contains(command);
    public bool HasRecipient => PhoneNumber is not null || RecipientMatches.Count > 0 || UnmatchedRecipientName is not null;

    /// <summary>The utterance looks like a money-transfer request (verb, or both an amount and a recipient).</summary>
    public bool LooksLikeSend => HasSendVerb || (AmountMinor is not null && HasRecipient);
}

/// <summary>
/// Deterministic, bilingual intent extraction: English, Twi and code-switched speech. This is not an LLM
/// and cannot move money; it only produces facts that are then validated, read back to the user, confirmed,
/// authenticated and submitted through the transaction state machine.
///
/// Why not a strict grammar: the live Khaya test turned "Fa Ghana sidi aduonum kɔma Ama" into
/// "Fahana si de aduonum ma Ama." Currency words get garbled; number words and names survive. So the
/// parser looks for numbers, phone numbers and the user's own contact names anywhere in the utterance.
/// </summary>
public static class IntentParser
{
    public static ParsedUtterance Parse(string? utterance, IReadOnlyList<Contact> contacts)
    {
        var tokens = TextNormalizer.Tokenize(utterance ?? "");
        var normalized = TextNormalizer.Join(tokens);
        var used = new HashSet<int>();

        var phone = FindPhoneNumber(tokens, used);
        var numbers = NumberWords.FindAll(tokens, used);
        foreach (var n in numbers)
            for (var i = n.Start; i < n.End; i++) used.Add(i);

        var commands = FindCommands(tokens);
        var hasVerb = tokens.Any(Lexicon.SendVerbs.Contains);
        var instead = tokens.Any(Lexicon.InsteadWords.Contains);
        var amount = PickAmount(tokens, numbers);

        var (matches, unmatched) = FindRecipients(tokens, used, contacts, phone is not null);

        int? ordinal = null;
        foreach (var t in tokens)
            if (Lexicon.Ordinals.TryGetValue(t, out var o)) { ordinal = o; break; }

        return new ParsedUtterance(normalized, commands, hasVerb, instead, numbers, amount, phone, matches, unmatched, ordinal);
    }

    private static HashSet<Command> FindCommands(IReadOnlyList<string> tokens)
    {
        var found = new HashSet<Command>();
        foreach (var (command, phrases) in Lexicon.Commands)
        {
            foreach (var phrase in phrases)
            {
                if (ContainsPhrase(tokens, phrase.Split(' '))) { found.Add(command); break; }
            }
        }
        return found;
    }

    private static bool ContainsPhrase(IReadOnlyList<string> tokens, string[] phrase)
    {
        for (var i = 0; i + phrase.Length <= tokens.Count; i++)
        {
            var match = true;
            for (var j = 0; j < phrase.Length && match; j++) match = tokens[i + j] == phrase[j];
            if (match) return true;
        }
        return false;
    }

    /// <summary>
    /// A Ghana mobile number written in digits ("0244123456", "024 412 3456", "+233 24...") or spoken digit by
    /// digit ("zero two four four ..."), in English or Twi. Its tokens are then excluded from amount parsing.
    /// </summary>
    private static Msisdn? FindPhoneNumber(IReadOnlyList<string> tokens, HashSet<int> used)
    {
        // Written digits, possibly split into groups by the recogniser.
        for (var start = 0; start < tokens.Count; start++)
        {
            if (!IsDigits(tokens[start])) continue;
            var digits = "";
            for (var end = start; end < tokens.Count && IsDigits(tokens[end]); end++)
            {
                digits += tokens[end];
                if (digits.Length >= 10 && Msisdn.TryParse(digits, out var number))
                {
                    for (var i = start; i <= end; i++) used.Add(i);
                    return number;
                }
                if (digits.Length > 12) break;
            }
        }

        // Spoken digit by digit.
        for (var start = 0; start < tokens.Count; start++)
        {
            var digits = "";
            var end = start;
            while (end < tokens.Count && NumberWords.SingleDigit(tokens[end]) is { } d) { digits += d; end++; }
            if (digits.Length >= 9 && (Msisdn.TryParse(digits, out var number) || Msisdn.TryParse("0" + digits, out number)))
            {
                for (var i = start; i < end; i++) used.Add(i);
                return number;
            }
        }
        return null;
    }

    private static bool IsDigits(string token) => token.Length > 0 && token.All(char.IsAsciiDigit);

    /// <summary>The amount: the number next to a currency word if there is one, else the first positive number.</summary>
    private static long? PickAmount(IReadOnlyList<string> tokens, IReadOnlyList<NumberSpan> numbers)
    {
        if (numbers.Count == 0) return null;
        foreach (var n in numbers)
        {
            var before = n.Start > 0 && NumberWords.IsCurrencyWord(tokens[n.Start - 1]);
            var after = n.End < tokens.Count && NumberWords.IsCurrencyWord(tokens[n.End]);
            if ((before || after) && n.ValueMinor > 0) return n.ValueMinor;
        }
        return numbers.FirstOrDefault(n => n.ValueMinor > 0)?.ValueMinor;
    }

    private static (IReadOnlyList<RecipientMatch> Matches, string? Unmatched) FindRecipients(
        IReadOnlyList<string> tokens, HashSet<int> used, IReadOnlyList<Contact> contacts, bool hasPhone)
    {
        var candidates = new List<(int Index, string Token)>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (used.Contains(i) || t.Length < 2 || char.IsAsciiDigit(t[0])) continue;
            if (NumberWords.IsCurrencyWord(t) || NumberWords.IsPesewaWord(t) || NumberWords.IsNumberWord(t)) continue;
            if (Lexicon.SendVerbs.Contains(t) || Lexicon.InsteadWords.Contains(t)) continue;
            candidates.Add((i, t));
        }
        var words = candidates.Select(c => c.Token).ToList();

        var matches = new List<RecipientMatch>();
        foreach (var contact in contacts)
        {
            var score = 0;
            var nameTokens = TextNormalizer.Tokenize(contact.DisplayName);
            if (nameTokens.Count > 0 && nameTokens.All(n => words.Any(w => Similar(w, n)))) score = 2;
            else if (contact.AliasList.Any(a => TextNormalizer.Tokenize(a) is { Count: > 0 } at && at.All(n => words.Any(w => Similar(w, n))))) score = 2;
            else if (nameTokens.Any(n => words.Any(w => Similar(w, n)))) score = 1;
            if (score > 0) matches.Add(new RecipientMatch(contact, score));
        }
        if (matches.Count > 0)
        {
            var best = matches.Max(m => m.Score);
            return (matches.Where(m => m.Score == best).ToList(), null);
        }
        if (hasPhone) return ([], null);

        // No contact matched: remember the name that was said after "to"/"kɔma", so we can say "I could not find Kojo".
        for (var k = 0; k < candidates.Count; k++)
        {
            var (index, _) = candidates[k];
            if (index > 0 && Lexicon.RecipientMarkers.Contains(tokens[index - 1]) && !Lexicon.Filler.Contains(tokens[index]) && !IsCommandWord(tokens[index]))
            {
                var name = tokens[index];
                if (k + 1 < candidates.Count && candidates[k + 1].Index == index + 1 && !Lexicon.Filler.Contains(candidates[k + 1].Token) && !IsCommandWord(candidates[k + 1].Token))
                    name += " " + candidates[k + 1].Token;
                return ([], Capitalize(name));
            }
        }
        return ([], null);
    }

    private static bool IsCommandWord(string token) =>
        Lexicon.Commands.Values.Any(phrases => phrases.Contains(token));

    /// <summary>Exact, or one letter off for names of 4+ letters (recognisers often slip one letter: "Kwami"/"Kwame").</summary>
    private static bool Similar(string heard, string name) =>
        heard == name || (name.Length >= 4 && heard.Length >= 4 && TextNormalizer.Distance(heard, name, 1) <= 1);

    private static string Capitalize(string s) =>
        string.Join(' ', s.Split(' ').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));
}
