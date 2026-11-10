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

    /// <summary>App language code ("en", "tw"). Remembered so the user is greeted in their language.</summary>
    public string PreferredLanguage { get; private set; } = "en";
    /// <summary>Spoken-response speed, 0.5 - 1.5. Slower speech helps many elderly users.</summary>
    public decimal SpeechRate { get; private set; } = 0.9m;
    /// <summary>After Kasa Sika asks a question, re-open the microphone automatically (eyes-free use).</summary>
    public bool HandsFree { get; private set; } = true;

    /// <summary>Seeded demonstration account. Demo reset only ever touches these users' data.</summary>
    public bool IsDemo { get; private set; }

    public static User Create(string displayName, Msisdn msisdn, string passcodeHash, DateTimeOffset now, bool isDemo = false) => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = displayName,
        Msisdn = msisdn.International,
        PasscodeHash = passcodeHash,
        CreatedAt = now,
        IsDemo = isDemo,
    };

    public bool IsLocked(DateTimeOffset now) => LockedUntil is { } until && until > now;

    /// <summary>Counts a wrong passcode; after <paramref name="maxFailures" /> the account is locked for a while</summary>
    public void RecordFailedAttempt(DateTimeOffset now, int maxFailures, TimeSpan lockDuration)
    {
        FailedAuthCount++;
        if (FailedAuthCount >= maxFailures) LockedUntil = now + lockDuration;
    }

    public int RemainingAttempts(int maxFailures) => Math.Max(0, maxFailures - FailedAuthCount);

    public void RecordSuccessfulAttempt()
    {
        FailedAuthCount = 0;
        LockedUntil = null;
    }

    /// <summary>Demo reset only: clears lockout so a presenter is never stuck after showing a failure.</summary>
    public void ClearLockout()
    {
        FailedAuthCount = 0;
        LockedUntil = null;
    }

    /// <summary>only used to upgrade the stored hash when the hashing parameters are strengthened </summary>
    public void ReplacePasscodeHash(string newHash) => PasscodeHash = newHash;

    public void UpdatePreferences(string? language, decimal? speechRate, bool? handsFree)
    {
        if (language is not null) PreferredLanguage = language;
        if (speechRate is { } rate) SpeechRate = Math.Clamp(rate, 0.5m, 1.5m);
        if (handsFree is { } hf) HandsFree = hf;
    }
}
