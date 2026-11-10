using System.Globalization;

namespace KasaSika.Application.Language;

/// <summary>A number found in an utterance: its value in minor units (pesewas) and the tokens it used.</summary>
/// <param name="IsDigitString">Written with digits ("50"), as opposed to words ("fifty", "aduonum").</param>
public sealed record NumberSpan(long ValueMinor, int Start, int Length, bool IsDigitString)
{
    public int End => Start + Length;
    public long WholeValue => ValueMinor / 100;
}

/// <summary>
/// Reads amounts in English and Twi, including the forms speech recognisers actually produce: digits
/// ("50", "12.50"), English words ("one hundred and fifty"), Twi words ("aduonum", "ɔha ne aduonum",
/// "mpem mmienu"), and mixtures ("5 hundred").
///
/// Twi forms are folded (ɛ→e, ɔ→o) and include common recogniser spellings. Twi numerals and their variants
/// REQUIRE NATIVE-SPEAKER REVIEW before production; see docs/integrations/khaya.md.
/// </summary>
public static class NumberWords
{
    private static readonly Dictionary<string, long> EnglishUnits = new()
    {
        ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6,
        ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12,
        ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17,
        ["eighteen"] = 18, ["nineteen"] = 19,
    };

    private static readonly Dictionary<string, long> EnglishTens = new()
    {
        ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fourty"] = 40, ["fifty"] = 50, ["sixty"] = 60,
        ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
    };

    /// <summary>Twi numerals (counting and adjectival forms) with recogniser spelling variants. Values are whole units.</summary>
    private static readonly Dictionary<string, long> Twi = new()
    {
        ["hwee"] = 0,
        ["baako"] = 1, ["biako"] = 1, ["baeko"] = 1,
        ["mmienu"] = 2, ["mienu"] = 2, ["abien"] = 2, ["abienu"] = 2, ["mmienun"] = 2,
        ["mmiensa"] = 3, ["miensa"] = 3, ["abiesa"] = 3, ["mmiesa"] = 3,
        ["enan"] = 4, ["anan"] = 4, ["nnan"] = 4,
        ["enum"] = 5, ["anum"] = 5, ["nnum"] = 5,
        ["nsia"] = 6, ["asia"] = 6,
        ["nson"] = 7, ["ason"] = 7,
        ["nwotwe"] = 8, ["awotwe"] = 8, ["nwotwi"] = 8,
        ["nkron"] = 9, ["akron"] = 9, ["nkoron"] = 9,
        ["edu"] = 10, ["du"] = 10,
        ["dubaako"] = 11, ["dumienu"] = 12, ["dummienu"] = 12, ["dumiensa"] = 13, ["dunan"] = 14, ["dunum"] = 15,
        ["dunsia"] = 16, ["dunson"] = 17, ["dunwotwe"] = 18, ["dunkron"] = 19,
        ["aduonu"] = 20, ["aduanu"] = 20, ["aduonnu"] = 20,
        ["aduasa"] = 30, ["aduasaa"] = 30,
        ["aduanan"] = 40, ["aduonan"] = 40,
        ["aduonum"] = 50, ["aduonnum"] = 50, ["aduanum"] = 50, ["aduonom"] = 50,
        ["aduosia"] = 60, ["aduasia"] = 60,
        ["aduoson"] = 70, ["aduason"] = 70,
        ["aduowotwe"] = 80, ["aduawotwe"] = 80,
        ["aduokron"] = 90, ["aduakron"] = 90,
        ["oha"] = 100, ["ohaa"] = 100,
        ["ahannu"] = 200, ["ahanu"] = 200, ["ahasa"] = 300, ["ahanan"] = 400, ["ahanum"] = 500,
        ["ahansia"] = 600, ["ahanson"] = 700, ["ahanwotwe"] = 800, ["ahankron"] = 900,
        ["apem"] = 1000,
    };

    /// <summary>Twi "thousands" multiplier: "mpem mmienu" = 2,000.</summary>
    private const string TwiThousands = "mpem";

    private static readonly HashSet<string> CurrencyWords =
    [
        "cedi", "cedis", "cidi", "sidi", "sedi", "cedie", "ghs", "ghana", "gh",
    ];

    private static readonly HashSet<string> PesewaWords = ["pesewa", "pesewas", "pesewaa", "p"];

    public static bool IsCurrencyWord(string token) => CurrencyWords.Contains(token);

    public static bool IsPesewaWord(string token) => PesewaWords.Contains(token);

    public static bool IsNumberWord(string token) =>
        EnglishUnits.ContainsKey(token) || EnglishTens.ContainsKey(token) || Twi.ContainsKey(token)
        || token is "hundred" or "thousand" or TwiThousands;

    /// <summary>A single spoken digit (for phone numbers said digit by digit: "zero two four four...").</summary>
    public static int? SingleDigit(string token)
    {
        if (token.Length == 1 && char.IsAsciiDigit(token[0])) return token[0] - '0';
        if (token == "oh") return 0; // "oh two four..." - only inside a phone number, never an amount
        if (EnglishUnits.TryGetValue(token, out var e) && e <= 9) return (int)e;
        if (Twi.TryGetValue(token, out var t) && t <= 9) return (int)t;
        return null;
    }

    /// <summary>Finds every number in the token list, left to right. Pesewas after cedis are folded in.</summary>
    public static IReadOnlyList<NumberSpan> FindAll(IReadOnlyList<string> tokens, ISet<int>? skip = null)
    {
        var spans = new List<NumberSpan>();
        var i = 0;
        while (i < tokens.Count)
        {
            if (skip?.Contains(i) == true) { i++; continue; }
            var span = ReadAt(tokens, i, skip);
            if (span is null) { i++; continue; }

            // "50 cedis 20 pesewas" / "50 cedis and 20 pesewas" → 50.20
            var next = span.End;
            while (next < tokens.Count && (IsCurrencyWord(tokens[next]) || tokens[next] is "and" or "ne")) next++;
            if (span.ValueMinor % 100 == 0 && next > span.End && next < tokens.Count)
            {
                var pesewas = ReadAt(tokens, next, skip);
                if (pesewas is not null && pesewas.End < tokens.Count && IsPesewaWord(tokens[pesewas.End]) && pesewas.WholeValue < 100)
                {
                    span = span with { ValueMinor = span.ValueMinor + pesewas.WholeValue, Length = pesewas.End + 1 - span.Start };
                }
            }
            spans.Add(span);
            i = span.End;
        }
        return spans;
    }

    private static NumberSpan? ReadAt(IReadOnlyList<string> tokens, int start, ISet<int>? skip)
    {
        var token = tokens[start];
        if (token.Length > 0 && char.IsAsciiDigit(token[0]))
            return ReadDigits(tokens, start);
        if (EnglishUnits.ContainsKey(token) || EnglishTens.ContainsKey(token) || (token == "a" && start + 1 < tokens.Count && tokens[start + 1] is "hundred" or "thousand"))
            return ReadEnglish(tokens, start, skip);
        if (Twi.ContainsKey(token) || token == TwiThousands)
            return ReadTwi(tokens, start, skip);
        return null;
    }

    private static NumberSpan? ReadDigits(IReadOnlyList<string> tokens, int start)
    {
        if (!decimal.TryParse(tokens[start], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) return null;
        if (decimal.Round(value, 2) != value || value > 100_000_000m) return null;
        var length = 1;
        // "5 hundred", "2 thousand"
        if (start + 1 < tokens.Count && decimal.Truncate(value) == value)
        {
            if (tokens[start + 1] == "hundred") { value *= 100; length++; }
            if (start + length < tokens.Count && tokens[start + length] == "thousand") { value *= 1000; length++; }
        }
        return new NumberSpan((long)(value * 100m), start, length, true);
    }

    private static NumberSpan? ReadEnglish(IReadOnlyList<string> tokens, int start, ISet<int>? skip)
    {
        long total = 0, current = 0;
        var i = start;
        var consumedAny = false;
        while (i < tokens.Count && skip?.Contains(i) != true)
        {
            var t = tokens[i];
            if (EnglishUnits.TryGetValue(t, out var unit))
            {
                // "one two" is two numbers (digits), not "three": stop at a second bare unit.
                if (consumedAny && current % 10 != 0 && current < 100) break;
                if (consumedAny && current >= 10 && current < 20) break;
                current += unit;
            }
            else if (EnglishTens.TryGetValue(t, out var tens))
            {
                if (consumedAny && current % 100 != 0) break;
                current += tens;
            }
            else if (t == "hundred") current = Math.Max(current, 1) * 100;
            else if (t == "thousand") { total += Math.Max(current, 1) * 1000; current = 0; }
            else if (t == "a" && !consumedAny) { i++; continue; }
            else if (t == "and" && consumedAny && i + 1 < tokens.Count && (EnglishUnits.ContainsKey(tokens[i + 1]) || EnglishTens.ContainsKey(tokens[i + 1])) && (total + current) >= 100) { i++; continue; }
            else break;
            consumedAny = true;
            i++;
        }
        if (!consumedAny) return null;
        return new NumberSpan((total + current) * 100, start, i - start, false);
    }

    private static NumberSpan? ReadTwi(IReadOnlyList<string> tokens, int start, ISet<int>? skip)
    {
        long total = 0;
        var i = start;
        var consumedAny = false;
        while (i < tokens.Count && skip?.Contains(i) != true)
        {
            var t = tokens[i];
            if (t == TwiThousands)
            {
                // "mpem mmienu" = 2,000; "mpem du" = 10,000
                var multiplier = i + 1 < tokens.Count && Twi.TryGetValue(tokens[i + 1], out var m) && m < 1000 ? m : 1;
                total += multiplier * 1000;
                i += multiplier == 1 ? 1 : 2;
                consumedAny = true;
                continue;
            }
            if (Twi.TryGetValue(t, out var value))
            {
                total += value; // Twi composes by addition: "aduonu baako" = 21, "oha ne aduonum" = 150
                consumedAny = true;
                i++;
                continue;
            }
            if (t == "ne" && consumedAny && i + 1 < tokens.Count && (Twi.ContainsKey(tokens[i + 1]) || tokens[i + 1] == TwiThousands)) { i++; continue; }
            break;
        }
        if (!consumedAny) return null;
        return new NumberSpan(total * 100, start, i - start, false);
    }
}
