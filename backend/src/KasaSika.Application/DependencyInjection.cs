using KasaSika.Application.Abstractions;
using KasaSika.Application.Auth;
using KasaSika.Application.Conversations;
using KasaSika.Application.Demo;
using KasaSika.Application.Options;
using KasaSika.Application.Speech;
using KasaSika.Application.Transactions;
using KasaSika.Domain.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KasaSika.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddKasaApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.Section));
        services.Configure<LimitsOptions>(configuration.GetSection(LimitsOptions.Section));
        services.Configure<ReconciliationOptions>(configuration.GetSection(ReconciliationOptions.Section));
        services.Configure<PaymentsOptions>(configuration.GetSection(PaymentsOptions.Section));
        services.Configure<DemoOptions>(configuration.GetSection(DemoOptions.Section));
        services.Configure<LanguageOptions>(configuration.GetSection(LanguageOptions.Section));

        services.AddSingleton(TimeProvider.System);

        services.AddScoped<AuthService>();
        services.AddScoped<TransactionService>();
        services.AddScoped<TransactionOutcomes>();
        services.AddScoped<ProviderStatusSync>();
        services.AddScoped<ReconciliationService>();
        services.AddScoped<TransactionQueries>();
        services.AddScoped<ConversationService>();
        services.AddScoped<SpeechService>();
        services.AddScoped(sp => new DemoService(
            sp.GetRequiredService<IKasaDbContext>(),
            sp.GetRequiredService<IPasscodeHasher>(),
            sp.GetService<IDemoPaymentControls>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DemoOptions>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DemoService>>()));

        services.AddScoped<IStepUpAuthenticator, PasscodeStepUpAuthenticator>();
        services.AddScoped<IStepUpAuthenticator, DeviceBiometricStepUpAuthenticator>();
        // The demo authenticator is only registered when configuration explicitly allows it; StartupValidation
        // additionally refuses to start if it is allowed with a non-test payment provider.
        var allowed = configuration.GetSection(AuthOptions.Section).Get<AuthOptions>()?.EffectiveStepUpMethods ?? [];
        if (allowed.Contains(AuthenticationMethod.DemoSimulated))
            services.AddScoped<IStepUpAuthenticator, DemoStepUpAuthenticator>();

        return services;
    }
}
