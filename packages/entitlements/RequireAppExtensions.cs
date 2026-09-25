using AppPlatform.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AppPlatform.Entitlements;

public static class RequireAppExtensions
{
    /// <summary>
    /// Refuses the request unless the caller's tenant has licensed <paramref name="app"/>.
    ///
    /// Enforced as a filter, not in handlers. A handler that checked its own entitlement would
    /// be one forgotten check away from a hole nobody can see in review — the same reason
    /// tenancy is a query filter rather than a WHERE clause each feature remembers to add.
    ///
    /// The shell hides unlicensed apps and never downloads their code, but that is a
    /// convenience. THIS is the access control.
    /// </summary>
    public static TBuilder RequireApp<TBuilder>(this TBuilder builder, string app)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(app);

        if (!Apps.All.Contains(app))
        {
            // Fails at startup rather than at request time. A typo'd app name would otherwise
            // match no entitlement and refuse every caller — an endpoint that is unreachable
            // for everyone, discovered by a customer.
            throw new ArgumentException(
                $"'{app}' is not a known app. Add it to Apps.All, and to the shell's registry.",
                nameof(app));
        }

        builder.Add(endpoint => endpoint.Metadata.Add(new RequiredAppMetadata(app)));

        return builder;
    }
}

public sealed record RequiredAppMetadata(string App);

/// <summary>
/// Checks the entitlement metadata on the matched endpoint. Placed after authentication, so
/// there is a caller whose licensed apps can be read.
/// </summary>
public sealed class EntitlementMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ICallerContext callerContext)
    {
        ArgumentNullException.ThrowIfNull(context);

        var required = context.GetEndpoint()?.Metadata.GetMetadata<RequiredAppMetadata>();

        if (required is not null)
        {
            var caller = callerContext.Caller;

            // An unauthenticated request is left to authorization to reject, so the reader is
            // told to sign in rather than that their organisation has not licensed something.
            if (caller is not null && !caller.LicensedApps.Contains(required.App))
            {
                // 403, not 404: the endpoint exists and the tenant could license it. A 404 here
                // would send an administrator hunting for a broken deployment.
                await AuthProblemResponse.WriteAsync(context, AuthProblem.AppNotLicensed);
                return;
            }
        }

        await next(context);
    }
}
