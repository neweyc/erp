using System.Net;
using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Entitlements;
using AppPlatform.Ledger.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

namespace AppPlatform.Ledger.Tests;

/// <summary>
/// Every ledger endpoint refuses a tenant that has not licensed the ledger — through the real route
/// table and the real entitlement middleware, so a new endpoint that forgets .RequireApp(Apps.Ledger)
/// fails here without anyone having to remember to add a case.
/// </summary>
public class EntitlementTests
{
    private sealed class FixedCaller(Caller caller) : ICallerContext
    {
        public Caller? Caller => caller;
        public Caller Require() => caller;
    }

    /// <summary>A real tenant with a real session — licensed for tickets, but not for the ledger.</summary>
    private static Caller TicketsOnly() => Fake.Caller() with { LicensedApps = [Apps.Tickets] };

    [Fact]
    public async Task Every_ledger_endpoint_answers_an_unlicensed_tenant_with_403()
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<ICallerContext>(new FixedCaller(TicketsOnly()));
                    // Never reached: the middleware answers before a handler runs.
                    services.AddSingleton(Mock.Of<ILedgerService>());
                    services.AddSingleton(Mock.Of<Outbox.IOutbox>());
                    services.AddSingleton(TimeProvider.System);
                    services.AddRouting();
                    services.AddAuthorization();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseMiddleware<EntitlementMiddleware>();
                    app.UseEndpoints(endpoints => endpoints.MapEndpoints(typeof(Data.LedgerDbContext).Assembly));
                }))
            .StartAsync();

        var routes = host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .ToList();

        // Eight today. Asserted so that a discovery failure cannot pass this test by finding none.
        Assert.Equal(8, routes.Count);

        var client = host.GetTestClient();
        foreach (var route in routes)
        {
            var method = route.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()!.HttpMethods.Single();
            var path = route.RoutePattern.RawText!.Replace("{entryId}", Ids.PublicId.New("je").ToString());

            var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
            {
                Content = method == "GET" ? null : new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            });

            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {path} answered {response.StatusCode}");
            Assert.Contains("app_not_licensed", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }
}
