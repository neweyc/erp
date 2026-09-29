using System.Text.Json;
using AppPlatform.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AppPlatform.ErrorFeed;

/// <summary>
/// The last line for a request: an unhandled exception becomes a 500 carrying a stable
/// <c>problemCode</c> and a reference, instead of the framework's default page.
///
/// The customer sees only the reference ("err_…") to quote to support. The full exception goes to
/// this service's own log under that reference, and the operator feed gets the metadata (through
/// <see cref="ErrorFeedLoggerProvider"/>, because this logs at Error), so one reference finds the
/// occurrence in the feed and the detail in the log.
///
/// Placed FIRST in the pipeline, so it also catches failures in authentication and the other
/// middleware. The tenant is read after the fact from the request's <see cref="ITenantProvider"/>,
/// which the authentication middleware has filled in by then, if it got that far.
/// </summary>
public sealed class ErrorFeedMiddleware(RequestDelegate next, ILogger<ErrorFeedMiddleware> logger)
{
    public const string ProblemCode = "internal_error";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await next(context);
        }
        catch (Exception exception) when (!context.RequestAborted.IsCancellationRequested)
        {
            // A client that went away is not a fault; that case is let through untouched.
            var reference = ErrorReference.New();
            var tenantId = context.RequestServices.GetService<ITenantProvider>()?.TenantId;

            // The structured property names are what the feed's logger reads.
            logger.LogError(exception,
                "Unhandled exception {ErrorReference} on {Method} {Path} (tenant {TenantId})",
                reference, context.Request.Method, context.Request.Path.Value, tenantId);

            // Once the response has started, a status can no longer be sent. Rethrown so the server
            // aborts the response, rather than leaving the client with a truncated success.
            if (context.Response.HasStarted) throw;

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(
                new { problemCode = ProblemCode, reference },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
    }
}
