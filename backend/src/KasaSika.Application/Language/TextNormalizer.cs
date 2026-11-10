using System.Globalization;
using System.Text;

namespace KasaSika.Application.Language;

/// <summary>
/// Turns what a recogniser produced into comparable tokens: lower case, Twi letters folded to ASCII
/// (ɛ→e, ɔ→o), accents and punctuation removed. Speech recognisers disagree on spelling ("ɔha"/"oha",
/// "Ɛsi"/"Esi"), so every comparison in the parser happens on this form.
/// </summary>
public static class TextNormalizer
{
    public const int MaxUtteranceLength = 300;

    public static string Fold(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(ch switch
            {
                'ɛ' or 'Ɛ' => 'e',
                'ɔ' or 'Ɔ' => 'o',
                'ŋ' or 'Ŋ' => 'n',
                '’' or '‘' or '`' => '\'',
                _ => char.ToLowerInvariant(ch),
            });
        }
        return sb.ToString();
    }

    /// <summary>
    /// Tokens of letters/digits. Keeps a decimal point between digits ("12.50") and splits currency symbols
    /// off amounts ("gh₵50" → "ghs", "50"; "50gh" → "50", "ghs").
    /// </summary>
    public static IReadOnlyList<string> Tokenize(string text)
    {
        var folded = Fold(text.Length > MaxUtteranceLength ? text[..MaxUtteranceLength] : text)
            .Replace("gh₵", " ghs ", StringComparison.Ordinal)
            .Replace("ghc", " ghs ", StringComparison.Ordinal)
            .Replace("₵", " ghs ", StringComparison.Ordinal)
            .Replace("¢", " ghs ", StringComparison.Ordinal);

        var tokens = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < folded.Length; i++)
        {
            var ch = folded[i];
            var isDecimalPoint = (ch == '.' || ch == ',') && current.Length > 0 && char.IsAsciiDigit(current[^1])
                                 && i + 1 < folded.Length && char.IsAsciiDigit(folded[i + 1]);
            if (char.IsLetterOrDigit(ch) || isDecimalPoint)
            {
                // Boundary between digits and letters: "50cedis" → "50" "cedis".
                if (current.Length > 0 && char.IsAsciiDigit(current[^1]) != char.IsAsciiDigit(ch) && !isDecimalPoint && current[^1] != '.' && current[^1] != ',')
                    Flush();
                current.Append(ch == ',' ? ',' : ch);
            }
            else if (ch == '\'')
            {
                continue; // "w'akyi" → "wakyi", "what's" → "whats"
            }
            else
            {
                Flush();
            }
        }
        Flush();
        return tokens;

        void Flush()
        {
            if (current.Length == 0) return;
            var token = current.ToString();
            // "1,000" is a thousands separator, "12,50" a decimal comma: keep digits only for the former.
            if (token.Contains(',') && token.Split(',')[^1].Length == 3) token = token.Replace(",", "", StringComparison.Ordinal);
            else token = token.Replace(',', '.');
            tokens.Add(token == "gh" ? "ghs" : token);
            current.Clear();
        }
    }

    public static string Join(IEnumerable<string> tokens) => string.Join(' ', tokens);

    /// <summary>Levenshtein distance, capped for speed. Used to tolerate one-letter recognition slips in names.</summary>
    public static int Distance(string a, string b, int cap = 3)
    {
        if (Math.Abs(a.Length - b.Length) > cap) return cap + 1;
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            var rowMin = curr[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                rowMin = Math.Min(rowMin, curr[j]);
            }
            if (rowMin > cap) return cap + 1;
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }
}
