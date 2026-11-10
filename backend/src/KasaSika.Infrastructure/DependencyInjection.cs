using KasaSika.Application.Abstractions;
using KasaSika.Application.Options;
using KasaSika.Application.Speech;
using KasaSika.Infrastructure.Background;
using KasaSika.Infrastructure.Payments;
using KasaSika.Infrastructure.Payments.Mtn;
using KasaSika.Infrastructure.Persistence;
using KasaSika.Infrastructure.Security;
using KasaSika.Infrastructure.Speech;
using KasaSika.Infrastructure.Speech.Khaya;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace KasaSika.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddKasaInfrastructure(this IServiceCollection services, IConfiguration configuration, string contentRoot)
    {
        services.Configure<SecurityOptions>(configuration.GetSection(SecurityOptions.Section));
        services.Configure<MtnOptions>(configuration.GetSection(MtnOptions.Section));
        services.Configure<KhayaOptions>(configuration.GetSection(KhayaOptions.Section));

        // ---- PostgreSQL: application state (never a mirror of wallet balances).
        services.AddDbContext<KasaDbContext>(o => o
            .UseNpgsql(configuration.GetConnectionString("KasaSika"), npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention());
        services.AddScoped<IKasaDbContext>(sp => sp.GetRequiredService<KasaDbContext>());

        // ---- Security
        services.AddSingleton<IPasscodeHasher, Pbkdf2PasscodeHasher>();
        services.AddSingleton<IFieldProtector, AesGcmFieldProtector>();
        services.AddSingleton<ITokenService, JwtTokenService>();

        // ---- Payments: exactly one provider is active; both go through the same state machine.
        var provider = configuration.GetSection(PaymentsOptions.Section).Get<PaymentsOptions>()?.Provider ?? "Mock";
        services.AddHttpClient(nameof(MockPaymentProvider), c => c.Timeout = TimeSpan.FromSeconds(10));
        if (provider.Equals("MtnSandbox", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<MtnTokenCache>();
            services.AddHttpClient(MtnTokenCache.HttpClientName, ConfigureMtn);
            services.AddHttpClient<MtnMomoProvider>(ConfigureMtn);
            services.AddScoped<IPaymentProvider>(sp => sp.GetRequiredService<MtnMomoProvider>());
            services.AddTransient<MtnSandboxProvisioner>();
        }
        else
        {
            services.AddSingleton<MockPaymentProvider>();
            services.AddSingleton<IPaymentProvider>(sp => sp.GetRequiredService<MockPaymentProvider>());
            services.AddSingleton<IDemoPaymentControls>(sp => sp.GetRequiredService<MockPaymentProvider>());
        }

        // ---- Ghanaian-language speech (Khaya). Replaceable: the application depends only on the interfaces.
        services.AddHttpClient<KhayaClient>((sp, c) =>
        {
            var o = sp.GetRequiredService<IOptions<KhayaOptions>>().Value;
            c.BaseAddress = new Uri(o.BaseUrl);
            c.Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds);
        });
        services.AddScoped<ISpeechRecognitionProvider, KhayaSpeechRecognitionProvider>();
        services.AddScoped<ITranslationProvider, KhayaTranslationProvider>();
        services.AddScoped<ITextToSpeechProvider, KhayaTextToSpeechProvider>();
        services.AddSingleton<ILanguageRegistry, ConfiguredLanguageRegistry>();
        services.AddSingleton<ISpeechClipStore>(sp =>
        {
            var dir = sp.GetRequiredService<IOptions<KhayaOptions>>().Value.CacheDirectory;
            return new FileSpeechClipStore(Path.IsPathRooted(dir) ? dir : Path.Combine(contentRoot, dir));
        });

        services.AddHostedService<ReconciliationWorker>();
        return services;
    }

    private static void ConfigureMtn(IServiceProvider sp, HttpClient client)
    {
        var o = sp.GetRequiredService<IOptions<MtnOptions>>().Value;
        client.BaseAddress = new Uri(o.BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds);
        client.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", o.SubscriptionKey);
    }
}
