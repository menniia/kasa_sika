namespace KasaSika.Domain.Contacts;

/// <summary>
/// A person the user sends money to. Resolving "Ama" to a number happens only against the user's own
/// contacts, and the resolved number is always read back before confirmation.
/// </summary>
public sealed class Contact
{
    private Contact() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string DisplayName { get; private set; } = "";
    /// <summary>Extra spoken forms, comma separated (e.g. "Maame Ama"). Lower-case, no diacritics.</summary>
    public string Aliases { get; private set; } = "";
    /// <summary>International digits; encrypted at rest.</summary>
    public string Msisdn { get; private set; } = "";
    public string MsisdnMasked { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? LastUsedAt { get; private set; }
    public int UseCount { get; private set; }

    public static Contact Create(Guid userId, string displayName, Msisdn msisdn, IEnumerable<string> aliases, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        DisplayName = displayName.Trim(),
        Aliases = string.Join(",", aliases.Select(a => a.Trim()).Where(a => a.Length > 0)),
        Msisdn = msisdn.International,
        MsisdnMasked = msisdn.Masked,
        CreatedAt = now,
    };

    public Msisdn Number => Domain.Msisdn.FromStored(Msisdn);

    public string LastFour => MsisdnMasked[^4..];

    public IEnumerable<string> AliasList => Aliases.Split(',', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Remembers frequent recipients (only after money actually went to them).</summary>
    public void RecordUse(DateTimeOffset now)
    {
        UseCount++;
        LastUsedAt = now;
    }

    public void ResetUsage()
    {
        UseCount = 0;
        LastUsedAt = null;
    }
}
