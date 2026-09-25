using AppPlatform.Tenancy;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AppPlatform.Auth;

public static class AuthenticationExtensions
{
    /// <summary>
    /// Registers the caller/tenant bridge and the cookie scheme. Call
    /// <see cref="UseAppPlatformAuth"/> to insert the middleware in the right order.
    /// </summary>
    public static IServiceCollection AddAppPlatformAuth(
        this IServiceCollection services, SessionCookie cookie)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(cookie);

        services.AddSingleton(cookie);
        services.TryAddTimeProvider();

        // One instance per scope, resolved through three interfaces. The tenant that query
        // filters and insert stamping use is therefore THE SAME OBJECT the authentication
        // middleware populated — there is no second path by which a tenant could be set.
        services.AddScoped<CallerContext>();
        services.AddScoped<ICallerContext>(sp => sp.GetRequiredService<CallerContext>());
        services.AddScoped<ITenantProvider>(sp => sp.GetRequiredService<CallerContext>());
        services.AddScoped<IBackgroundTenantScope>(sp => sp.GetRequiredService<CallerContext>());
        services.AddScoped<ICookieAuthenticationState>(sp => sp.GetRequiredService<CallerContext>());

        services.AddAuthentication(cookie.SchemeName)
            .AddCookie(cookie.SchemeName, options =>
            {
                options.Cookie.Name = cookie.Name;
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Lax;

                // Host-only. A domain-wide cookie would be presented to the operator
                // console as well, which is exactly the isolation this must not break.
                options.Cookie.Domain = null;

                // An API returns 401, never a 302 to a login page: a redirect turns a
                // failed fetch into opaque HTML the client cannot interpret.
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });

        return services;
    }

    /// <summary>
    /// Order is the substance of this method, not a detail.
    ///
    /// Authentication establishes the caller; CSRF needs to know whether the request is
    /// cookie-authenticated (a bearer key needs no token); suspension is authorization and
    /// must see a caller that authentication has already accepted. Any other order either
    /// checks CSRF against an unknown principal or refuses a suspended tenant before it
    /// has an identity to authorize.
    /// </summary>
    public static IApplicationBuilder UseAppPlatformAuth(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseAuthentication();
        app.UseMiddleware<SessionAuthenticationMiddleware>();
        app.UseMiddleware<CsrfMiddleware>();
        app.UseMiddleware<SuspensionMiddleware>();

        return app;
    }

    private static void TryAddTimeProvider(this IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(TimeProvider))) return;
        services.AddSingleton(TimeProvider.System);
    }
}
