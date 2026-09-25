using AppPlatform.Auth;

namespace AppPlatform.Platform.Auth;

public interface IOperatorContext
{
    OperatorPrincipal? Principal { get; }
    OperatorPrincipal Require();
}

public sealed class OperatorContext : IOperatorContext, ICookieAuthenticationState
{
    private OperatorPrincipal? _principal;

    /// <summary>
    /// Operators authenticate by cookie only — there is no operator API key — so any
    /// authenticated operator request carries CSRF risk and must present a token.
    /// </summary>
    public bool IsCookieAuthenticated => _principal is not null;


    public OperatorPrincipal? Principal => _principal;

    public OperatorPrincipal Require() => _principal
        ?? throw new InvalidOperationException(
            "No authenticated operator in this scope. Reaching a handler without one is a " +
            "wiring fault, not a 401 — endpoints that need an operator require authorization.");

    public void Set(OperatorPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (_principal is not null)
            throw new InvalidOperationException("The operator for this scope has already been set.");

        _principal = principal;
    }
}
