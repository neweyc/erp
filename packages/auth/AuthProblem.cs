namespace AppPlatform.Auth;

/// <summary>
/// Stable problem codes. The UI branches on these strings, never on a message, so they are
/// API surface: renaming one is a breaking change.
///
/// A single generic 401 for all of them is a support burden — a user cannot tell "your
/// admin changed your role" from "your session expired" from "your organisation's account
/// is on hold", and each needs a different next step.
/// </summary>
public static class AuthProblem
{
    public const string SessionInvalid = "session_invalid";
    public const string SessionRevoked = "session_revoked";
    public const string SessionIdleTimeout = "session_idle_timeout";
    public const string RoleChanged = "role_changed";
    public const string UserDeactivated = "user_deactivated";
    public const string MfaRequired = "mfa_required";
    public const string TenantRetired = "tenant_retired";

    /// <summary>403, not 401 — the principal survives. See <see cref="Caller.TenantSuspended"/>.</summary>
    public const string TenantSuspended = "tenant_suspended";

    /// <summary>403. The tenant has not licensed the app this endpoint belongs to.</summary>
    public const string AppNotLicensed = "app_not_licensed";

    /// <summary>403. Missing or mismatched CSRF token on an unsafe method.</summary>
    public const string CsrfFailed = "csrf_failed";
}
