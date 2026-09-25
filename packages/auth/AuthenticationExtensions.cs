using AppPlatform.Audit;
using AppPlatform.Tenancy;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
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
        this IServiceCollection services, SessionCookie cookie, string? dataProtectionKeyPath = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(cookie);

        services.AddSingleton(cookie);
        services.TryAddTimeProvider();
        services.AddSharedDataProtection(cookie, dataProtectionKeyPath);

        // One instance per scope, resolved through three interfaces. The tenant that query
        // filters and insert stamping use is therefore THE SAME OBJECT the authentication
        // middleware populated — there is no second path by which a tenant could be set.
        services.AddScoped<CallerContext>();
        services.AddScoped<ICallerContext>(sp => sp.GetRequiredService<CallerContext>());
        services.AddScoped<ITenantProvider>(sp => sp.GetRequiredService<CallerContext>());
        services.AddScoped<IBackgroundTenantScope>(sp => sp.GetRequiredService<CallerContext>());
        services.AddScoped<ICookieAuthenticationState>(sp => sp.GetRequiredService<CallerContext>());
        services.AddScoped<IAuditActor>(sp => sp.GetRequiredService<CallerContext>());
        services.AddScoped<IAuditActorScope>(sp => sp.GetRequiredService<CallerContext>());

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

    /// <summary>
    /// One key ring across every process that shares a cookie.
    ///
    /// **This is not a tuning detail — without it the cookie simply does not work across
    /// services.** The auth cookie is encrypted by Data Protection, and by default each process
    /// derives its own keys from its own application name and content root. A cookie issued by
    /// core.api is then undecryptable by tickets.api, which rejects it as invalid: the user signs
    /// in, sees the shell, and every call to an app API answers 401. Nothing in either service's
    /// own tests can see it, because each is correct in isolation.
    ///
    /// Two parts, both required: a shared application NAME so the key derivation matches, and a
    /// PERSISTED key ring so the keys survive a restart — with an in-memory ring, every deploy
    /// silently signs out every user.
    ///
    /// Operator and tenant surfaces get DIFFERENT purposes, so a console cookie can never be
    /// decrypted as a tenant one even though both rings live in the same place.
    /// </summary>
    private static void AddSharedDataProtection(
        this IServiceCollection services, SessionCookie cookie, string? keyPath)
    {
        var builder = services.AddDataProtection()
            .SetApplicationName($"app-platform:{cookie.SchemeName}");

        if (keyPath is { Length: > 0 })
        {
            Directory.CreateDirectory(keyPath);
            builder.PersistKeysToFileSystem(new DirectoryInfo(keyPath));
        }
    }

    private static void TryAddTimeProvider(this IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(TimeProvider))) return;
        services.AddSingleton(TimeProvider.System);
    }
}
