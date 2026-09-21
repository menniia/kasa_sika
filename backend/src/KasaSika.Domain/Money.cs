using System.Globalization;

namespace KasaSika.Domain;

public sealed record Money
{
    public long AmountMinor { get; }
    public string Currency { get; }

    private Money(long amountMinor, string currency)
    {
        AmountMinor = amountMinor;
        Currency = currency;
    }

    public static Money FromMinor(long amountMinor, string currency)
    {
        if (amountMinor <= 0)
            throw KasaException.Validation("MONEY_AMOUNT_MUST_BE_POSITIVE", "The amount must be greater than zero");
        if (currency is null || currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper))
            throw KasaException.Validation("MONEY_CURRENCY_INVALID", "The currency must be a 3-letter ISO code such as GHS.");
        return new Money(amountMinor, currency);
    }

    public static Money Ghs(long wholeCedis) => FromMinor(checked(wholeCedis * 100), "GHS");

    public string ToDecimalString()
    {
        var whole = AmountMinor / 100;
        var fraction = AmountMinor % 100;
        return fraction == 0
            ? whole.ToString(CultureInfo.InvariantCulture)
            : $"{whole.ToString(CultureInfo.InvariantCulture)}.{fraction.ToString("00", CultureInfo.InvariantCulture)}";
    }

    /// <summary>Text meant to be read aloud by text-to-speech </summary>
    public string ToSpeech()
    {
        var whole = AmountMinor / 100;
        var fraction = AmountMinor % 100;
        if (Currency != "GHS")
            return $"{ToDecimalString()} {Currency}";
        return fraction == 0
            ? $"{whole} Ghana cedis"
            : $"{whole} Ghana cedis and {fraction} pesewas";
    }

    public override string ToString() => $"{ToDecimalString()} {Currency}";
}
