using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AppPlatform.Auth.Tests;

/// <summary>
/// The real limiter and forwarded-header handling, in a real (in-process) host with endpoint
/// routing, so the part that finds anonymous endpoints by their metadata is exercised too.
/// </summary>
public class AnonymousRateLimitingTests
{
    /// <summary>
    /// The test host has no socket, so it has no client address. This stands in for the network:
    /// it sets the address the connection "came from". It runs before forwarded headers, as the
    /// connection itself does.
    /// </summary>
    private const string SimulatedSourceHeader = "X-Test-Source-Address";

    private static async Task<IHost> StartAsync(
        int permitsPerMinute = 3, string? trustedProxies = null, string environment = "Development")
    {
        var settings = new Dictionary<string, string?>
        {
            [AnonymousRateLimiting.PermitsPerMinuteKey] = permitsPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [AnonymousRateLimiting.TrustedProxiesKey] = trustedProxies,
        };

        return await new HostBuilder()
            .UseEnvironment(environment)
            .ConfigureAppConfiguration(config => config.AddInMemoryCollection(settings))
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices((context, services) =>
                {
                    services.AddRouting();
                    services.AddAnonymousRateLimiting(context.Configuration, context.HostingEnvironment);
                })
                .Configure(app =>
                {
                    app.Use(async (context, next) =>
                    {
                        if (context.Request.Headers.TryGetValue(SimulatedSourceHeader, out var source))
                            context.Connection.RemoteIpAddress = IPAddress.Parse(source.ToString());
                        await next();
                    });

                    // Routing first, as WebApplication does implicitly, so the limiter can see
                    // which endpoint a request is for.
                    app.UseRouting();
                    app.UseAnonymousRateLimiting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapPost("/sign-in", () => Results.Ok()).AllowAnonymous();
                        endpoints.MapGet("/not-anonymous", () => Results.Ok());
                        endpoints.MapGet("/seen-as", (HttpContext c) => c.Connection.RemoteIpAddress?.ToString()).AllowAnonymous();
                    });
                }))
            .StartAsync();
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string source, string? forwardedFor = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(SimulatedSourceHeader, source);
        if (forwardedFor is not null) request.Headers.Add("X-Forwarded-For", forwardedFor);
        return request;
    }

    private static async Task<List<HttpStatusCode>> SignInAttemptsAsync(
        HttpClient client, int count, string source, string? forwardedFor = null)
    {
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < count; i++)
        {
            using var response = await client.SendAsync(Request(HttpMethod.Post, "/sign-in", source, forwardedFor));
            statuses.Add(response.StatusCode);
        }
        return statuses;
    }

    [Fact]
    public async Task An_anonymous_endpoint_is_refused_past_the_limit_with_a_problem_code_and_retry_after()
    {
        using var host = await StartAsync(permitsPerMinute: 3);
        var client = host.GetTestClient();

        Assert.All(await SignInAttemptsAsync(client, 3, "203.0.113.1"), s => Assert.Equal(HttpStatusCode.OK, s));

        using var refused = await client.SendAsync(Request(HttpMethod.Post, "/sign-in", "203.0.113.1"));

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.True(refused.Headers.RetryAfter?.Delta > TimeSpan.Zero, "Retry-After tells the client when to try again");
        var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(AuthProblem.RateLimited, body.GetProperty("problemCode").GetString());
    }

    [Fact]
    public async Task An_endpoint_that_is_not_anonymous_is_not_limited_here()
    {
        using var host = await StartAsync(permitsPerMinute: 3);
        var client = host.GetTestClient();

        for (var i = 0; i < 10; i++)
        {
            using var response = await client.SendAsync(Request(HttpMethod.Get, "/not-anonymous", "203.0.113.1"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task Each_client_address_has_its_own_limit()
    {
        using var host = await StartAsync(permitsPerMinute: 3);
        var client = host.GetTestClient();

        await SignInAttemptsAsync(client, 4, "203.0.113.1");

        Assert.All(await SignInAttemptsAsync(client, 3, "203.0.113.2"), s => Assert.Equal(HttpStatusCode.OK, s));
    }

    [Fact]
    public async Task Behind_a_trusted_proxy_the_forwarded_client_address_is_what_is_limited()
    {
        // Without this, every user arrives from the proxy's address and shares one limit.
        using var host = await StartAsync(permitsPerMinute: 3, trustedProxies: "10.0.0.5");
        var client = host.GetTestClient();

        await SignInAttemptsAsync(client, 4, source: "10.0.0.5", forwardedFor: "198.51.100.1");

        Assert.All(
            await SignInAttemptsAsync(client, 3, source: "10.0.0.5", forwardedFor: "198.51.100.2"),
            s => Assert.Equal(HttpStatusCode.OK, s));

        using var seen = await client.SendAsync(Request(HttpMethod.Get, "/seen-as", "10.0.0.5", "198.51.100.3"));
        Assert.Equal("198.51.100.3", await seen.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_forwarded_address_from_an_untrusted_source_is_ignored()
    {
        // Anyone can send X-Forwarded-For. Believed from anywhere, it would give an attacker a
        // fresh address, and a fresh limit, on every request.
        using var host = await StartAsync(permitsPerMinute: 3, trustedProxies: "10.0.0.5");
        var client = host.GetTestClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            statuses.AddRange(await SignInAttemptsAsync(client, 1, source: "203.0.113.9", forwardedFor: $"198.51.100.{i}"));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);

        using var seen = await client.SendAsync(Request(HttpMethod.Get, "/seen-as", "203.0.113.10", "198.51.100.50"));
        Assert.Equal("203.0.113.10", await seen.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Loopback_is_not_trusted_unless_it_is_listed()
    {
        // The framework trusts loopback as a proxy by default; that default is cleared, so a
        // process on the same host cannot choose its own address either.
        using var host = await StartAsync(trustedProxies: "10.0.0.5");
        var client = host.GetTestClient();

        using var seen = await client.SendAsync(Request(HttpMethod.Get, "/seen-as", "127.0.0.1", "198.51.100.50"));

        Assert.Equal("127.0.0.1", await seen.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task With_no_trusted_proxy_a_forwarded_address_is_never_believed()
    {
        // The framework reads an EMPTY trust list as "trust everyone". With nothing configured,
        // rotating X-Forwarded-For must not buy a fresh limit.
        using var host = await StartAsync(permitsPerMinute: 3, trustedProxies: null);
        var client = host.GetTestClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            statuses.AddRange(await SignInAttemptsAsync(client, 1, source: "203.0.113.9", forwardedFor: $"198.51.100.{i}"));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);

        using var seen = await client.SendAsync(Request(HttpMethod.Get, "/seen-as", "203.0.113.10", "198.51.100.50"));
        Assert.Equal("203.0.113.10", await seen.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Rotating_through_one_IPv6_64_shares_one_limit()
    {
        using var host = await StartAsync(permitsPerMinute: 3);
        var client = host.GetTestClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 1; i <= 4; i++)
        {
            statuses.AddRange(await SignInAttemptsAsync(client, 1, source: $"2001:db8:1:2::{i:x}"));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);

        // A different /64 is a different client.
        Assert.All(await SignInAttemptsAsync(client, 3, "2001:db8:1:3::1"), s => Assert.Equal(HttpStatusCode.OK, s));
    }

    [Theory]
    [InlineData("203.0.113.1", "203.0.113.1")]
    [InlineData("::ffff:203.0.113.1", "203.0.113.1")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    public void Client_keys_group_equivalent_addresses(string address, string expected)
        => Assert.Equal(expected, AnonymousRateLimiting.ClientKey(IPAddress.Parse(address)));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_limit_below_one_is_refused_at_startup(int permits)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(permitsPerMinute: permits));

        Assert.Contains(AnonymousRateLimiting.PermitsPerMinuteKey, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Production_refuses_to_start_without_a_trusted_proxy()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartAsync(environment: "Production"));

        Assert.Contains(AnonymousRateLimiting.TrustedProxiesKey, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_trusted_proxy_that_is_not_an_address_is_refused_at_startup()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartAsync(trustedProxies: "nginx"));

        Assert.Contains("not an IP address", error.Message, StringComparison.Ordinal);
    }
}
