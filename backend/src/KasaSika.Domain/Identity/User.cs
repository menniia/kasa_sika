using static System.Runtime.InteropServices.JavaScript.JSType;
namespace KasaSika.Domain.Identity;

public sealed class User
{
    private User() { }

    public Guid Id { get; private set; }
    public string DisplayName { get; private set; } = "";

    /// <summary>
    /// Login identifier, international digits. (prototype): ownership of this number is not verified yet
    /// (no SMS one-time code at registration). not going to be treated as proof that the user controls the SIM
    /// </summary>
    public string Msisdn { get; private set; } = "";

    /// <summary>
    /// Hash of Kasa Sika passcode (PBKDF2 via ASP.NET Core Identity's PasswordHasher). This is not the
    /// user's MoMo PIN, which Kasa Sika never sees, stores or transmits 
    /// </summary>
    public string PasscodeHash { get; private set; } = "";

    public int FailedAuthCount { get; private set; }
    public DateTimeOffset? LockedUntil { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static User Create(string displayName, Msisdn msisdn, string passcodeHash, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = displayName,
        PasscodeHash = passcodeHash,
        CreatedAt = now,
    };

    public bool IsLocked(DateTimeOffset now) => LockedUntil is { } until && until > now;

    /// <summary>Counts a wrong passcode; after <paramref name="maxFailures" /> the account is locked for a while</summary>
    public void RecordFailedAttempt(DateTimeOffset now, int maxFailures, TimeSpan lockDuration)
    {
        FailedAuthCount++;
        if (FailedAuthCount >= maxFailures) LockedUntil = now + lockDuration;
    }

    public void RecordSuccessfulAttempt()
    {
        FailedAuthCount = 0;
        LockedUntil = null;
    }

    /// <summary>only used to upgrade the stored hash when the hashing parameters are strengthened </summary>
    public void ReplacePasscodeHash(string newHash) => PasscodeHash = newHash;
}