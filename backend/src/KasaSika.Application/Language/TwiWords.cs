namespace KasaSika.Application.Language;

/// <summary>
/// Writes numbers, digits and dates as Twi words, so the Twi voice says "Ghana sidi aduonum" rather than
/// trying to read "50". REQUIRES NATIVE-SPEAKER REVIEW before production.
/// </summary>
public static class TwiWords
{
    private static readonly string[] Units = ["", "baako", "mmienu", "mmiɛnsa", "ɛnan", "enum", "nsia", "nson", "nwɔtwe", "nkron"];
    private static readonly string[] Teens = ["du", "dubaako", "dumienu", "dumiɛnsa", "dunan", "dunum", "dunsia", "dunson", "dunwɔtwe", "dunkron"];
    private static readonly string[] Tens = ["", "", "aduonu", "aduasa", "aduanan", "aduonum", "aduosia", "aduoson", "aduowɔtwe", "aduokron"];
    private static readonly string[] Hundreds = ["", "ɔha", "ahannu", "ahasa", "ahanan", "ahanum", "ahansia", "ahanson", "ahanwɔtwe", "ahankron"];
    private static readonly string[] Digits = ["hwee", "baako", "mmienu", "mmiɛnsa", "ɛnan", "enum", "nsia", "nson", "nwɔtwe", "nkron"];
    private static readonly string[] Months =
        ["Ɔpɛpɔn", "Ogyefuo", "Ɔbɛnem", "Oforisuo", "Kɔtɔnimma", "Ayɛwohomumu", "Kutawonsa", "Ɔsanaa", "Ɛbɔ", "Ahinime", "Obubuo", "Ɔpɛnimma"];

    public static string Number(long n)
    {
        if (n <= 0) return "hwee";
        if (n >= 1_000_000) return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var parts = new List<string>();
        var thousands = n / 1000;
        if (thousands == 1) parts.Add("apem");
        else if (thousands > 1) parts.Add("mpem " + Below1000(thousands));
        var rest = n % 1000;
        if (rest > 0) parts.Add(Below1000(rest));
        return string.Join(" ne ", parts);
    }

    private static string Below1000(long n)
    {
        var parts = new List<string>();
        if (n / 100 > 0) parts.Add(Hundreds[n / 100]);
        var rest = n % 100;
        if (rest > 0) parts.Add(Below100(rest));
        return string.Join(" ne ", parts);
    }

    private static string Below100(long n)
    {
        if (n < 10) return Units[n];
        if (n < 20) return Teens[n - 10];
        return n % 10 == 0 ? Tens[n / 10] : $"{Tens[n / 10]} {Units[n % 10]}";
    }

    /// <summary>"3456" → "mmiɛnsa, ɛnan, enum, nsia" — digits read one by one, as phone digits are.</summary>
    public static string DigitByDigit(string digits) =>
        string.Join(", ", digits.Where(char.IsAsciiDigit).Select(c => Digits[c - '0']));

    public static string Month(int month) => Months[month - 1];
}
