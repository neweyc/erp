using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AppPlatform.Auth.Tests;

/// <summary>
/// Sign-in followed by a protected mutation, over one client that keeps its cookies — the
/// journey a real browser makes.
///
/// Testing issuance and enforcement separately is what let the gap through: each half was
/// correct, and together they signed a user in and then refused everything they did.
/// </summary>
public class CsrfIssuanceTests
{
    private sealed class Authenticated(bool value) : ICookieAuthenticationState
    {
        public bool IsCookieAuthenticated => value;
    }

    private static async Task<IHost> HostAsync(bool authenticated)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton<ICookieAuthenticationState>(new Authenticated(authenticated));
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.Map("/sign-in", branch => branch.Run(context =>
                    {
                        CsrfToken.Issue(context);
                        return context.Response.WriteAsync("signed in");
                    }));
                    app.UseMiddleware<CsrfMiddleware>();
                    app.Run(context => context.Response.WriteAsync("mutated"));
                }))
            .StartAsync();

        return host;
    }

    [Fact]
    public async Task Sign_in_issues_a_token_cookie()
    {
        using var host = await HostAsync(authenticated: true);

        var response = await host.GetTestClient().GetAsync("/sign-in");

        Assert.Contains(
            response.Headers.GetValues("Set-Cookie"),
            c => c.StartsWith($"{CsrfPolicy.CookieName}=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_issued_token_is_readable_by_script()
    {
        using var host = await HostAsync(authenticated: true);

        var response = await host.GetTestClient().GetAsync("/sign-in");
        var cookie = response.Headers.GetValues("Set-Cookie")
            .Single(c => c.StartsWith($"{CsrfPolicy.CookieName}=", StringComparison.Ordinal));

        // NOT HttpOnly: the page must copy it into a header, which is the whole mechanism of
        // double-submit. Its protection comes from same-origin, not from secrecy.
        Assert.DoesNotContain("httponly", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_mutation_succeeds_when_the_issued_token_is_echoed_back()
    {
        using var host = await HostAsync(authenticated: true);
        var client = host.GetTestClient();

        var signIn = await client.GetAsync("/sign-in");
        var token = signIn.Headers.GetValues("Set-Cookie")
            .Single(c => c.StartsWith($"{CsrfPolicy.CookieName}=", StringComparison.Ordinal))
            .Split(';')[0][(CsrfPolicy.CookieName.Length + 1)..];

        var request = new HttpRequestMessage(HttpMethod.Post, "/tenants/x/status");
        request.Headers.Add("Cookie", $"{CsrfPolicy.CookieName}={token}");
        request.Headers.Add(CsrfPolicy.HeaderName, token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Every_issued_token_survives_cookie_encoding_unchanged()
    {
        using var host = await HostAsync(authenticated: true);
        var client = host.GetTestClient();

        // Many, because the original bug was intermittent: plain base64 only breaks when the
        // random bytes happen to contain '+' or '/', so a single sample passes most of the time
        // and the failure reaches production as "sometimes nothing works".
        for (var i = 0; i < 200; i++)
        {
            var cookie = (await client.GetAsync("/sign-in")).Headers.GetValues("Set-Cookie")
                .Single(c => c.StartsWith($"{CsrfPolicy.CookieName}=", StringComparison.Ordinal));

            var value = cookie.Split(';')[0][(CsrfPolicy.CookieName.Length + 1)..];

            Assert.DoesNotContain('%', value);
            Assert.DoesNotContain('+', value);
            Assert.DoesNotContain('/', value);
        }
    }

    [Fact]
    public async Task A_mutation_without_the_token_is_still_refused()
    {
        using var host = await HostAsync(authenticated: true);

        var response = await host.GetTestClient().PostAsync("/tenants/x/status", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_mismatched_token_is_refused_even_when_both_are_present()
    {
        using var host = await HostAsync(authenticated: true);

        var request = new HttpRequestMessage(HttpMethod.Post, "/tenants/x/status");
        request.Headers.Add("Cookie", $"{CsrfPolicy.CookieName}=cookie-value");
        request.Headers.Add(CsrfPolicy.HeaderName, "header-value");

        // Presence is not the check — a cross-site caller can cause a cookie to be SENT but
        // cannot read it to echo it back.
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetTestClient().SendAsync(request)).StatusCode);
    }
}
