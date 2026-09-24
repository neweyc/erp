namespace AppPlatform.Auth.Tests;

public class CsrfPolicyTests
{
    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    [InlineData("TRACE")]
    [InlineData("get")]
    public void Safe_methods_need_no_token(string method)
        => Assert.Null(CsrfPolicy.Evaluate(method, isCookieAuthenticated: true, null, null));

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public void Every_unsafe_method_requires_a_token(string method)
    {
        // PUT and DELETE are IDEMPOTENT and change state. A rule written around
        // idempotency — the wording this replaced — leaves both unprotected, which is most
        // of a REST API's write surface.
        Assert.Equal(AuthProblem.CsrfFailed,
            CsrfPolicy.Evaluate(method, isCookieAuthenticated: true, null, null));
    }

    [Fact]
    public void A_matching_token_passes()
        => Assert.Null(CsrfPolicy.Evaluate("POST", true, "tok-abc", "tok-abc"));

    [Fact]
    public void A_mismatched_token_fails()
        => Assert.Equal(AuthProblem.CsrfFailed, CsrfPolicy.Evaluate("POST", true, "tok-abc", "tok-xyz"));

    [Fact]
    public void A_header_without_a_cookie_fails()
        => Assert.Equal(AuthProblem.CsrfFailed, CsrfPolicy.Evaluate("POST", true, "tok-abc", null));

    [Fact]
    public void An_empty_token_on_both_sides_does_not_count_as_a_match()
    {
        // Two empty strings are equal. Accepting them would let a request with no CSRF
        // protection at all pass the comparison.
        Assert.Equal(AuthProblem.CsrfFailed, CsrfPolicy.Evaluate("POST", true, "", ""));
    }

    [Fact]
    public void Key_authenticated_calls_need_no_token()
    {
        // A browser does not attach a bearer key on its own, so there is no cross-site
        // exposure to protect against — and requiring one would break every integration.
        Assert.Null(CsrfPolicy.Evaluate("DELETE", isCookieAuthenticated: false, null, null));
    }

    [Fact]
    public void Tokens_of_different_lengths_are_rejected_without_throwing()
    {
        // FixedTimeEquals requires equal-length spans; a naive call throws on a token the
        // attacker controls the length of, turning a 403 into a 500.
        Assert.Equal(AuthProblem.CsrfFailed, CsrfPolicy.Evaluate("POST", true, "short", "considerably-longer"));
    }
}
