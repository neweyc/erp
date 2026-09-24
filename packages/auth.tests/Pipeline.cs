using System.Net;
using System.Security.Claims;
using AppPlatform.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace AppPlatform.Auth.Tests;

/// <summary>A session store with no database, so the pipeline can be exercised in isolation.</summary>
internal sealed class StubSessionStore(SessionContext? context) : ISessionStore
{
    public int Touches { get; private set; }
    public bool ThrowOnTouch { get; set; }

    public Task<SessionContext?> FindAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => Task.FromResult(context);

    public Task TouchAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        Touches++;
        return ThrowOnTouch ? Task.FromException(new InvalidOperationException("db down")) : Task.CompletedTask;
    }
}

internal sealed record Probe(HttpStatusCode Status, string Body, int? TenantIdSeenByTenancy);

internal static class Pipeline
{
    /// <summary>
    /// Builds the real middleware chain over a terminal endpoint that reports the tenant
    /// <see cref="ITenantProvider"/> resolves to — which is the thing actually worth
    /// asserting, since that is what query filters and insert stamping read.
    /// </summary>
    public static async Task<Probe> SendAsync(
        SessionContext? stored,
        string path = "/api/tickets/v1/tickets",
        string method = "GET",
        Guid? sessionId = null,
        string? ticketRole = "admin",
        string? csrfHeader = null,
        string? csrfCookie = null,
        StubSessionStore? store = null,
        DateTimeOffset? now = null)
    {
        var sessions = store ?? new StubSessionStore(stored);

        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<TimeProvider>(new FakeTimeProvider(now ?? Build.Now));
                    services.AddSingleton<ISessionStore>(sessions);
                    services.AddAppPlatformAuth(SessionCookie.Tenant);
                    services.AddAuthorization();
                })
                .Configure(app =>
                {
                    // Stands in for the cookie handler: the claims a validated ticket would
                    // carry. The point under test is what the middleware does with them.
                    app.Use(async (context, next) =>
                    {
                        if (sessionId is { } id)
                        {
                            var claims = new List<Claim> { new(SessionCookie.SessionIdClaim, id.ToString()) };
                            if (ticketRole is not null) claims.Add(new Claim(SessionCookie.RoleClaim, ticketRole));
                            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
                        }
                        await next();
                    });

                    app.UseMiddleware<SessionAuthenticationMiddleware>();
                    app.UseMiddleware<CsrfMiddleware>();
                    app.UseMiddleware<SuspensionMiddleware>();

                    app.Run(async context =>
                    {
                        var tenant = context.RequestServices.GetRequiredService<ITenantProvider>();
                        await context.Response.WriteAsync(tenant.TenantId?.ToString() ?? "none");
                    });
                }))
            .StartAsync();

        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (csrfHeader is not null) request.Headers.Add(CsrfPolicy.HeaderName, csrfHeader);
        if (csrfCookie is not null) request.Headers.Add("Cookie", $"{CsrfPolicy.CookieName}={csrfCookie}");

        var response = await host.GetTestClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        return new Probe(response.StatusCode, body,
            int.TryParse(body, out var tenantId) ? tenantId : null);
    }

    public static readonly Guid SessionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
}
