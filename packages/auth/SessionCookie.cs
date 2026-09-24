namespace AppPlatform.Auth;

/// <summary>
/// Cookie naming and options. Operator and customer surfaces must never share a session,
/// so they get distinct names AND distinct hosts — the console runs on its own hostname
/// and its cookie is host-only, never domain-wide. A forged or replayed cookie from one
/// surface cannot authenticate the other.
/// </summary>
public sealed record SessionCookie(string Name, string SchemeName)
{
    public static readonly SessionCookie Tenant = new("ap_session", "ap.tenant");
    public static readonly SessionCookie Operator = new("ap_op", "ap.operator");

    /// <summary>The session id claim. Every cookie carries one; the row is revalidated per request.</summary>
    public const string SessionIdClaim = "ap:sid";

    /// <summary>
    /// The role at the time the ticket was issued, compared against the live row on every
    /// request. This is what makes a demotion take effect immediately rather than at
    /// cookie expiry.
    /// </summary>
    public const string RoleClaim = "ap:role";
}
