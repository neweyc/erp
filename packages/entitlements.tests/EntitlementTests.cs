using System.Net;
using AppPlatform.Auth;
using AppPlatform.Entitlements;
using AppPlatform.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AppPlatform.Entitlements.Tests;

/// <summary>
/// The entitlement gate, through the real pipeline.
///
/// Every licensed-app feature needs one of these: the shell hiding an app is a convenience, and
/// a test that only proves the nav item is absent proves nothing about whether a determined
/// caller — or someone else's script — can reach the endpoint anyway.
/// </summary>
public class EntitlementTests
{
    private sealed class FixedCaller(Caller? caller) : ICallerContext
    {
        public Caller? Caller => caller;
        public Caller Require() => caller ?? throw new InvalidOperationException("no caller");
    }

    private static Caller CallerWith(params string[] licensedApps) => new()
    {
        PrincipalId = Guid.NewGuid(),
        Kind = PrincipalKind.User,
        TenantId = 1,
        CompanyId = 1,
        Role = "member",
        LicensedApps = licensedApps,
    };

    private static async Task<(HttpStatusCode Status, string Body)> CallAsync(Caller? caller)
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<ICallerContext>(new FixedCaller(caller));
                    services.AddRouting();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseMiddleware<EntitlementMiddleware>();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/api/tickets/v1/tickets", () => Results.Ok(new { ok = true }))
                            .RequireApp(Apps.Tickets);

                        // Core is always on and carries no entitlement, so this must stay
                        // reachable for a tenant that has licensed nothing at all.
                        endpoints.MapGet("/api/core/v1/employees", () => Results.Ok(new { ok = true }));
                    });
                }))
            .StartAsync();

        var response = await host.GetTestClient().GetAsync("/api/tickets/v1/tickets");
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_licensed_tenant_reaches_the_endpoint()
        => Assert.Equal(HttpStatusCode.OK, (await CallAsync(CallerWith("tickets"))).Status);

    [Fact]
    public async Task An_unlicensed_tenant_gets_403_from_the_api()
    {
        var (status, body) = await CallAsync(CallerWith());

        // 403, not 404: the endpoint exists and the tenant could license it. A 404 would send
        // an administrator hunting for a broken deployment.
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains("app_not_licensed", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Licensing_a_different_app_does_not_open_this_one()
        => Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(CallerWith("something-else"))).Status);

    [Fact]
    public async Task An_unauthenticated_request_is_left_to_authorization()
    {
        // Not 403 app_not_licensed: telling an anonymous caller their organisation has not
        // licensed something answers a question they did not ask and leaks what exists. Sign-in
        // is the correct next step, and authorization is what says so.
        var (status, _) = await CallAsync(caller: null);

        Assert.NotEqual(HttpStatusCode.Forbidden, status);
    }

    [Fact]
    public async Task Registering_an_unknown_app_fails_at_startup()
    {
        // A typo'd name would match no entitlement and refuse every caller — an endpoint
        // unreachable for everyone, discovered by a customer rather than by a build.
        // AddRouting is required or the host fails for an unrelated reason and the assertion
        // passes on the WRONG exception — which is how a guard test quietly stops guarding.
        var builder = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services => services.AddRouting())
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                        endpoints.MapGet("/x", () => "ok").RequireApp("tickts"));
                }));

        await Assert.ThrowsAsync<ArgumentException>(() => builder.StartAsync());
    }

    [Fact]
    public async Task Core_is_not_a_licensable_app()
    {
        // A constant for core would invite .RequireApp(Apps.Core), which is then one
        // misconfigured row away from locking every tenant out of their own employee list.
        Assert.DoesNotContain("core", Apps.All);

        var builder = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services => services.AddRouting())
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapGet("/x", () => "ok").RequireApp("core"));
                }));

        await Assert.ThrowsAsync<ArgumentException>(() => builder.StartAsync());
    }
}
