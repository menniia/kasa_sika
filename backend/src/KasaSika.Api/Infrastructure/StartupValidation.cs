using KasaSika.Application.Options;
using KasaSika.Domain.Identity;
using KasaSika.Infrastructure.Payments.Mtn;
using KasaSika.Infrastructure.Security;

namespace KasaSika.Api.Infrastructure;

/// <summary>
/// Refuses to start in configurations that could mislead a user or move real money unintentionally.
/// Failing loudly at startup is far safer than discovering the problem during a demonstration.
/// </summary>
public static class StartupValidation
{
    public static void Validate(IConfiguration config)
    {
        var errors = new List<string>();

        var security = config.GetSection(SecurityOptions.Section).Get<SecurityOptions>() ?? new SecurityOptions();
        CheckKey(errors, "Security:JwtSigningKey", security.JwtSigningKey, 32, exact: false);
        CheckKey(errors, "Security:FieldEncryptionKey", security.FieldEncryptionKey, 32, exact: true);
        CheckKey(errors, "Security:FingerprintKey", security.FingerprintKey, 32, exact: false);

        if (string.IsNullOrWhiteSpace(config.GetConnectionString("KasaSika")))
            errors.Add("ConnectionStrings:KasaSika is not set.");

        var payments = config.GetSection(PaymentsOptions.Section).Get<PaymentsOptions>() ?? new PaymentsOptions();
        var isMock = payments.Provider.Equals("Mock", StringComparison.OrdinalIgnoreCase);
        var isSandbox = payments.Provider.Equals("MtnSandbox", StringComparison.OrdinalIgnoreCase);
        if (!isMock && !isSandbox)
            errors.Add($"Payments:Provider '{payments.Provider}' is not supported. This prototype runs only with 'Mock' or 'MtnSandbox'. Live MTN is not enabled.");

        if (isSandbox)
        {
            var mtn = config.GetSection(MtnOptions.Section).Get<MtnOptions>() ?? new MtnOptions();
            if (!mtn.TargetEnvironment.Equals("sandbox", StringComparison.Ordinal))
                errors.Add("Mtn:TargetEnvironment must be 'sandbox'. Production MTN is not enabled in this prototype.");
            if (!mtn.BaseUrl.Contains("sandbox", StringComparison.OrdinalIgnoreCase))
                errors.Add("Mtn:BaseUrl must be the MTN sandbox host.");
            if (!mtn.IsConfigured)
                errors.Add("MtnSandbox needs Mtn:SubscriptionKey, Mtn:ApiUser and Mtn:ApiKey (dotnet user-secrets). Run 'provision-mtn-sandbox' to create the API user.");
        }

        var auth = config.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();
        if (auth.EffectiveStepUpMethods.Contains(AuthenticationMethod.DemoSimulated) && !(isMock || isSandbox))
            errors.Add("DemoSimulated step-up authentication is only allowed with mock or sandbox payments.");

        var demo = config.GetSection(DemoOptions.Section).Get<DemoOptions>() ?? new DemoOptions();
        if (demo.Enabled && !(isMock || isSandbox))
            errors.Add("Demo mode is only allowed with mock or sandbox payments.");

        if (errors.Count > 0)
            throw new InvalidOperationException("Kasa Sika cannot start safely:\n - " + string.Join("\n - ", errors)
                + "\nSee README.md, 'Configure secrets'.");
    }

    private static void CheckKey(List<string> errors, string name, string value, int bytes, bool exact)
    {
        if (string.IsNullOrWhiteSpace(value)) { errors.Add($"{name} is not set (use dotnet user-secrets or an environment variable)."); return; }
        try
        {
            var length = Convert.FromBase64String(value).Length;
            if (exact ? length != bytes : length < bytes) errors.Add($"{name} must be {(exact ? "exactly" : "at least")} {bytes} bytes, base64-encoded.");
        }
        catch (FormatException)
        {
            errors.Add($"{name} must be base64-encoded.");
        }
    }
}
