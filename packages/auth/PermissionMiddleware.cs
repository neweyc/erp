using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AppPlatform.Auth;

public static class PermissionEndpointExtensions
{
    /// <summary>
    /// Refuses the request with 403 <c>not_permitted</c> unless the caller holds
    /// <paramref name="permission"/>. Enforced by <see cref="PermissionMiddleware"/>: handlers
    /// never check permissions, for the same reason they never check entitlements — a check each
    /// feature must remember is a hole nobody sees in review.
    /// </summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        // At startup, not per request: a mistyped permission matches nobody, so the endpoint
        // would refuse every caller and the first to notice would be a customer.
        if (!PermissionCatalogue.Names.Contains(permission))
        {
            throw new ArgumentException(
                $"'{permission}' is not in the catalogue. Add it to Permissions and PermissionCatalogue.",
                nameof(permission));
        }

        builder.Add(endpoint => endpoint.Metadata.Add(new RequiredPermissionMetadata(permission)));
        return builder;
    }

    /// <summary>
    /// For the few routes that concern only the caller themselves — the session, sign-out. Must be
    /// said explicitly: an authenticated endpoint that names no permission is refused, so leaving
    /// the declaration out cannot open a route to everyone.
    /// </summary>
    public static TBuilder AllowAnySignedInUser<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Add(endpoint => endpoint.Metadata.Add(new AnySignedInUserMetadata()));
        return builder;
    }
}

public sealed record RequiredPermissionMetadata(string Permission);

public sealed record AnySignedInUserMetadata;

/// <summary>
/// Checks the permission declared on the matched endpoint. Runs after authentication, CSRF and
/// suspension (see <see cref="AuthenticationExtensions.UseAppPlatformAuth"/>).
/// </summary>
public sealed class PermissionMiddleware(RequestDelegate next, ILogger<PermissionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, ICallerContext callerContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(callerContext);

        var endpoint = context.GetEndpoint();
        var caller = callerContext.Caller;

        // No caller: an anonymous route, or an unauthenticated request that authorization will
        // answer with 401 — "sign in" is the more useful message than "not permitted".
        if (endpoint is null || caller is null)
        {
            await next(context);
            return;
        }

        var problem = Decide(endpoint, caller);
        if (problem is null)
        {
            await next(context);
            return;
        }

        if (problem == AuthProblem.NotPermitted && IsUndeclared(endpoint))
        {
            // A wiring fault, not a user error: logged at Error so it reaches the operator feed.
            // PermissionCoverageTests should have caught it at build time.
            logger.LogError(
                "Endpoint {Endpoint} requires authentication but declares no permission; refused.",
                endpoint.DisplayName);
        }

        await AuthProblemResponse.WriteAsync(context, problem);
    }

    /// <summary>Null to allow, otherwise the problem code to refuse with.</summary>
    public static string? Decide(Endpoint endpoint, Caller caller)
    {
        var required = endpoint.Metadata.GetMetadata<RequiredPermissionMetadata>();

        if (required is not null)
        {
            if (caller.Permissions.Contains(required.Permission)) return null;

            // Same answer the entitlement check gives, whichever runs first. A permission in an
            // unlicensed app is never granted, and "not licensed" tells an administrator what to
            // fix where "not permitted" would send them to the Users page for nothing.
            var app = Permissions.AppOf(required.Permission);
            if (app != Permissions.CoreApp && !caller.LicensedApps.Contains(app))
                return AuthProblem.AppNotLicensed;

            return AuthProblem.NotPermitted;
        }

        if (endpoint.Metadata.GetMetadata<AnySignedInUserMetadata>() is not null) return null;

        // Anonymous routes need no permission; a signed-in caller may still reach them.
        if (!RequiresAuthentication(endpoint)) return null;

        return AuthProblem.NotPermitted;
    }

    private static bool IsUndeclared(Endpoint endpoint)
        => endpoint.Metadata.GetMetadata<RequiredPermissionMetadata>() is null
           && endpoint.Metadata.GetMetadata<AnySignedInUserMetadata>() is null;

    /// <summary>
    /// The framework's own definition: authorization metadata present and not overridden by
    /// AllowAnonymous. Used by <c>PermissionCoverageTests</c> too, so both agree on what counts.
    /// </summary>
    public static bool RequiresAuthentication(Endpoint endpoint)
        => endpoint.Metadata.GetMetadata<IAuthorizeData>() is not null
           && endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null;
}
