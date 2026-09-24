namespace AppPlatform.Auth;

public enum TenantStatus
{
    Active,
    Suspended,
    Retired,
}

/// <summary>
/// One row from <c>identity_v1.session_context(session_id)</c> — session, user, tenant
/// status, and entitlements resolved together.
///
/// A function rather than a view, because a grant on a view is a grant to read all of it
/// and a session id is credential-equivalent: <c>SELECT *</c> would enumerate every live
/// session in every tenant.
/// </summary>
public sealed record SessionContext
{
    public required Guid SessionId { get; init; }
    public required Guid UserId { get; init; }
    public required int TenantId { get; init; }
    public required int CompanyId { get; init; }
    public required TenantStatus TenantStatus { get; init; }
    public required string Role { get; init; }
    public required bool UserActive { get; init; }
    public required bool MfaSatisfied { get; init; }
    public required DateTimeOffset LastSeenAt { get; init; }
    public required DateTimeOffset AbsoluteExpiry { get; init; }

    public DateTimeOffset? RevokedAt { get; init; }
    public Guid? EmployeeId { get; init; }
    public IReadOnlyList<string> LicensedApps { get; init; } = [];

    /// <summary>Minutes of inactivity after which the session ends; null means no idle window.</summary>
    public int? IdleTimeoutMinutes { get; init; }
}
