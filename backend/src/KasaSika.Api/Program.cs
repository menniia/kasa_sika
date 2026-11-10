using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using KasaSika.Api.Infrastructure;
using KasaSika.Application;
using KasaSika.Application.Auth;
using KasaSika.Application.Demo;
using KasaSika.Infrastructure;
using KasaSika.Infrastructure.Payments.Mtn;
using KasaSika.Infrastructure.Persistence;
using KasaSika.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
// Optional, git-ignored overrides next to appsettings (secrets belong in user-secrets or environment variables).
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

StartupValidation.Validate(builder.Configuration);

builder.Services.AddKasaApplication(builder.Configuration);
builder.Services.AddKasaInfrastructure(builder.Configuration, builder.Environment.ContentRootPath);

builder.Services.AddControllers().AddJsonOptions(o =>
{
    // SCREAMING_SNAKE enums (READY_FOR_CONFIRMATION, SANDBOX): the contract the app renders.
    o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper));
    o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
});
builder.Services.AddOpenApi();

var security = builder.Configuration.GetSection(SecurityOptions.Section).Get<SecurityOptions>()!;
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = security.JwtIssuer,
        ValidAudience = security.JwtAudience,
        IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(security.JwtSigningKey)),
        ClockSkew = TimeSpan.FromSeconds(30),
    };
    o.Events = new JwtBearerEvents
    {
        // A token is only as good as its session row: logout / "lost phone" revocation takes effect immediately.
        OnTokenValidated = async context =>
        {
            var auth = context.HttpContext.RequestServices.GetRequiredService<AuthService>();
            var principal = context.Principal!;
            if (!await auth.IsSessionActiveAsync(principal.SessionId(), principal.UserId(), context.HttpContext.RequestAborted))
                context.Fail("Session is no longer active.");
        },
    };
});
builder.Services.AddAuthorization();

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [];
    if (builder.Environment.IsDevelopment()) p.SetIsOriginAllowed(_ => true);
    else p.WithOrigins(origins);
    p.AllowAnyHeader().AllowAnyMethod().WithExposedHeaders(RequestContext.CorrelationHeader);
}));

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    static string Key(HttpContext c) => c.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    o.AddPolicy("auth", c => RateLimitPartition.GetFixedWindowLimiter(Key(c), _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("callbacks", c => RateLimitPartition.GetFixedWindowLimiter(Key(c), _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("clips", c => RateLimitPartition.GetFixedWindowLimiter(Key(c), _ => new FixedWindowRateLimiterOptions { PermitLimit = 240, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

// ---- One-off commands -----------------------------------------------------------------------------------
if (args.Contains("provision-mtn-sandbox"))
{
    var host = args.SkipWhile(a => a != "provision-mtn-sandbox").Skip(1).FirstOrDefault() ?? "localhost";
    var provisioner = app.Services.GetService<MtnSandboxProvisioner>()
        ?? throw new InvalidOperationException("Set Payments:Provider=MtnSandbox and Mtn:SubscriptionKey first.");
    var (apiUser, apiKey) = await provisioner.ProvisionAsync(host, CancellationToken.None);
    Console.WriteLine("MTN sandbox API user created. Store these as secrets (never commit them):");
    Console.WriteLine($"  dotnet user-secrets set \"Mtn:ApiUser\" \"{apiUser}\" --project src/KasaSika.Api");
    Console.WriteLine($"  dotnet user-secrets set \"Mtn:ApiKey\" \"{apiKey}\" --project src/KasaSika.Api");
    return;
}

// ---- Database: migrate and seed the demo account --------------------------------------------------------
if (app.Configuration.GetValue("Database:MigrateOnStartup", false) || args.Contains("migrate"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<KasaDbContext>().Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<DemoService>().SeedAsync(CancellationToken.None);
    if (args.Contains("migrate")) return;
}

app.UseMiddleware<ErrorHandlingMiddleware>();
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Logger.LogInformation("Kasa Sika API started. Payments provider: {Provider}", app.Configuration["Payments:Provider"] ?? "Mock");
app.Run();

public partial class Program;
