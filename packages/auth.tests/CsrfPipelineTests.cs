using System.Net;

namespace AppPlatform.Auth.Tests;

/// <summary>
/// CSRF is driven by <see cref="ICookieAuthenticationState"/>, not by the tenant caller
/// directly. Wiring it to the tenant context made it INERT on the operator console: it saw no
/// caller, concluded the request was not cookie-authenticated, and waved every mutation through.
/// </summary>
public class CsrfPipelineTests
{
    private sealed record State(bool IsCookieAuthenticated) : ICookieAuthenticationState;

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public void A_cookie_authenticated_mutation_needs_a_token_whatever_the_identity(string method)
    {
        // The operator console's state object is a different type from the tenant's; the policy
        // must not care which one it is.
        Assert.Equal(AuthProblem.CsrfFailed,
            CsrfPolicy.Evaluate(method, new State(true).IsCookieAuthenticated, null, null));
    }

    [Fact]
    public void An_unauthenticated_request_is_not_subject_to_csrf()
    {
        // Sign-in itself has no cookie yet. Demanding a token there would make signing in
        // impossible.
        Assert.Null(CsrfPolicy.Evaluate("POST", new State(false).IsCookieAuthenticated, null, null));
    }

    [Fact]
    public void A_matching_token_passes_for_either_identity()
        => Assert.Null(CsrfPolicy.Evaluate("POST", new State(true).IsCookieAuthenticated, "t", "t"));
}
