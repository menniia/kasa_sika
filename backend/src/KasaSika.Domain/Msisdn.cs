using System.Text.RegularExpressions;

namespace KasaSika.Domain;

public sealed partial record Msisdn
{
    [GeneratedRegex(@"^(?:\+?233|0)([25]\d{8})$", RegexOptions.CultureInvariant)]

    private static partial Regex GhanaMobile();

    /// <summary>International digits, e.g 233240001234.</summary>
    
    public string International { get; }

    private Msisdn(string international) => International = international;

    public static bool TryParse(string? input, out Msisdn? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var compact = new string(input.Where(c => c is not (' ' or '-' or '(' or ')')).ToArray());
        var match = GhanaMobile().Match(compact);
        if (!match.Success) return false;
        result = new Msisdn("233" + match.Groups[1].Value);
        return true;
    }

    public static Msisdn Parse(string? input) =>
        TryParse(input, out var msisdn)
            ? msisdn!
            : throw KasaException.Validation("INVALID_MSISDN", "This is not a valid Ghana mobile number");

    public static Msisdn FromStored(string international) => Parse(international);

    public string Local => "0" + International[3..];

    /// <summary>Safe to display: "024XXX1234". Shows the network prefix and last 4 digits only</summary>
    public string Masked => Local[..3] + "XXX" + Local[^4..];

    public string LastFour => Local[^4..];

    public override string ToString() => Masked;
}