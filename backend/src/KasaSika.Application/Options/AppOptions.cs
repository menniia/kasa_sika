using KasaSika.Domain.Identity;

namespace KasaSika.Application.Options;

public sealed class AuthOptions
{
    public const string Section = "Authentication";

    /// <summary>Wrong passcodes allowed before the account is locked (login and per-transfer step-up share the counter).</summary>
    public int MaxFailedAttempts { get; set; } = 3;
    public int LockoutMinutes { get; set; } = 15;
    public int SessionHours { get; set; } = 8;
    /// <summary>Off by default: open registration is a deliberate, per-environment decision.</summary>
    public bool AllowSelfRegistration { get; set; }
    /// <summary>
    /// Step-up methods this server accepts. DemoSimulated is additionally refused at startup unless payments
    /// are mock or MTN sandbox.
    /// </summary>
    public List<AuthenticationMethod> StepUpMethods { get; set; } = [];

    /// <summary>Configured methods, or passcode + device biometric when none are configured. (A list default would be
    /// appended to by configuration binding, not replaced - hence the empty default.)</summary>
    public IReadOnlyList<AuthenticationMethod> EffectiveStepUpMethods =>
        StepUpMethods.Count > 0 ? StepUpMethods : [AuthenticationMethod.Passcode, AuthenticationMethod.DeviceBiometric];
    /// <summary>How long a per-transfer authentication challenge stays valid.</summary>
    public int ChallengeSeconds { get; set; } = 300;
}

public sealed class LimitsOptions
{
    public const string Section = "Limits";

    /// <summary>GHS 1,000.00 by default. Bounds the damage of a stolen session; real limits need a compliance decision.</summary>
    public long MaxTransactionMinor { get; set; } = 100_000;
    /// <summary>GHS 5,000.00 per rolling 24 hours per user, counting transfers that were sent or may have been.</summary>
    public long MaxDailyMinor { get; set; } = 500_000;
    /// <summary>How long a user has to walk through confirm -> authenticate -> submit. Generous: speech is slower than tapping.</summary>
    public int TransactionTtlMinutes { get; set; } = 10;
}

public sealed class ReconciliationOptions
{
    public const string Section = "Reconciliation";

    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 15;
    /// <summary>If MTN has no record of an uncertain submission this long after we lost track, treat it as never received.</summary>
    public int NotFoundGraceMinutes { get; set; } = 10;
    /// <summary>A transaction stuck in SUBMITTING this long means the process died mid-call.</summary>
    public int StaleSubmittingMinutes { get; set; } = 2;
    /// <summary>An UNKNOWN transfer still unresolved after this long is escalated to RECONCILIATION_REQUIRED (operator).</summary>
    public int EscalateUnknownAfterMinutes { get; set; } = 30;
    public int BatchSize { get; set; } = 50;
}

public sealed class PaymentsOptions
{
    public const string Section = "Payments";

    /// <summary>"Mock" (default, no network, no money) or "MtnSandbox". Live is not supported by this prototype.</summary>
    public string Provider { get; set; } = "Mock";
    /// <summary>Public base URL of this API, used to build the provider callback URL. Null = no callbacks requested.</summary>
    public string? CallbackBaseUrl { get; set; }
}

public sealed class DemoOptions
{
    public const string Section = "Demo";

    /// <summary>Seeds the demo account and exposes presenter controls (scenario, reset). Refused with a live provider.</summary>
    public bool Enabled { get; set; }
    public string UserName { get; set; } = "Akosua";
    public string UserPhone { get; set; } = "0240000001";
    /// <summary>Passcode of the SEEDED DEMO account only. It protects nothing real (mock/sandbox money).</summary>
    public string Passcode { get; set; } = "";
    /// <summary>Allow "Continue with demo account" without typing the passcode (the transfer step-up still applies).</summary>
    public bool AllowPasswordlessDemoLogin { get; set; } = true;
    public List<DemoContact> Contacts { get; set; } = [];
}

/// <param name="Scenario">Mock outcome when paying this contact: Success, Pending, Failure, Timeout, Unknown, DuplicateCallback, RecipientInvalid.</param>
public sealed class DemoContact
{
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
    public List<string> Aliases { get; set; } = [];
    public string Scenario { get; set; } = "Success";
}

public sealed class LanguageOptions
{
    public const string Section = "Languages";

    public string Default { get; set; } = "en";
    public List<LanguageConfig> Supported { get; set; } = [];
}

public sealed class LanguageConfig
{
    public string Code { get; set; } = "";
    public string EnglishName { get; set; } = "";
    public string NativeName { get; set; } = "";
    public bool Enabled { get; set; }
    public string ProviderCode { get; set; } = "";
    public string? DeviceLocale { get; set; }
    public bool ServerTts { get; set; }
    public bool ServerAsr { get; set; }
}
