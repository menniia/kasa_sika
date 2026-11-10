using System.Security.Claims;
using KasaSika.Domain;
using KasaSika.Infrastructure.Security;
using Microsoft.IdentityModel.JsonWebTokens;

namespace KasaSika.Api.Infrastructure;

public static class RequestContext
{
    public const string CorrelationHeader = "X-Correlation-Id";

    public static Guid UserId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id
            : throw KasaException.Unauthenticated("UNAUTHENTICATED", "Please sign in again.");

    public static Guid SessionId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(JwtTokenService.SessionClaim), out var id)
            ? id
            : throw KasaException.Unauthenticated("UNAUTHENTICATED", "Please sign in again.");

    public static Guid? DeviceId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(JwtTokenService.DeviceClaim), out var id) ? id : null;

    public static string CorrelationId(this HttpContext context) =>
        context.Items[CorrelationHeader] as string ?? context.TraceIdentifier;
}

/// <summary>
/// Turns every failure into a small, normalized JSON error: {"error": {"code", "message", "retryable"}}.
/// The app maps codes to plain-language speech; users never hear "HTTP 502" or "ECONNRESET".
/// Unexpected exceptions are logged with the correlation id and reported without internals.
/// </summary>
public sealed class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[RequestContext.CorrelationHeader].FirstOrDefault() is { Length: > 0 and <= 100 } supplied
            && supplied.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
                ? supplied
                : Guid.NewGuid().ToString("N");
        context.Items[RequestContext.CorrelationHeader] = correlationId;
        context.Response.Headers[RequestContext.CorrelationHeader] = correlationId;

        try
        {
            await next(context);
        }
        catch (KasaException ex)
        {
            if (context.Response.HasStarted) throw;
            context.Response.StatusCode = StatusFor(ex.Kind);
            object? details = ex.Data.Count > 0 ? ex.Data.Keys.Cast<object>().ToDictionary(k => k.ToString()!, k => ex.Data[k]) : null;
            await context.Response.WriteAsJsonAsync(new { error = new { code = ex.Code, message = ex.Message, retryable = ex.Retryable, details } });
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The caller went away; nothing to report.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error (correlation {CorrelationId})", correlationId);
            if (context.Response.HasStarted) throw;
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new
            {
                error = new { code = "INTERNAL_ERROR", message = "Something went wrong on our side. Please check the transaction status before trying again.", retryable = false },
            });
        }
    }

    public static int StatusFor(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => StatusCodes.Status400BadRequest,
        ErrorKind.Unauthenticated => StatusCodes.Status401Unauthorized,
        ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        ErrorKind.NotFound => StatusCodes.Status404NotFound,
        ErrorKind.Conflict => StatusCodes.Status409Conflict,
        ErrorKind.Gone => StatusCodes.Status410Gone,
        ErrorKind.Locked => StatusCodes.Status423Locked,
        ErrorKind.TooManyRequests => StatusCodes.Status429TooManyRequests,
        ErrorKind.ProviderFailure => StatusCodes.Status502BadGateway,
        ErrorKind.NotSupported => StatusCodes.Status501NotImplemented,
        ErrorKind.ServiceUnavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };
}
