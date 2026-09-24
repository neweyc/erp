using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace AppPlatform.Auth;

/// <summary>
/// Writes the failure body. One place, so every rejection carries a machine-readable
/// <c>problemCode</c> the shell can branch on — a UI matching on message text breaks the
/// first time someone improves the wording.
/// </summary>
public static class AuthProblemResponse
{
    /// <summary>
    /// Suspension and entitlement are AUTHORIZATION outcomes and keep the principal;
    /// everything else is an authentication failure.
    /// </summary>
    public static int StatusFor(string problemCode) => problemCode switch
    {
        AuthProblem.TenantSuspended or AuthProblem.AppNotLicensed or AuthProblem.CsrfFailed
            => StatusCodes.Status403Forbidden,
        _ => StatusCodes.Status401Unauthorized,
    };

    public static async Task WriteAsync(HttpContext context, string problemCode)
    {
        ArgumentNullException.ThrowIfNull(context);

        // 401, never a 302 to a login page: an API that redirects turns a failed fetch
        // into an opaque HTML response the client cannot interpret.
        context.Response.StatusCode = StatusFor(problemCode);
        context.Response.ContentType = "application/json";

        await context.Response.WriteAsync(JsonSerializer.Serialize(
            new { problemCode }, JsonOptions));
    }

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
}
