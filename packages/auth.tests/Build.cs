namespace AppPlatform.Auth.Tests;

internal static class Build
{
    public static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The access claim a cookie issued to a <c>core:admin</c> carries.</summary>
    public static readonly string AdminAccess = Access.Stamp(Access.Granted([BuiltInRoles.CoreAdmin], []));

    public static SessionContext Session(
        TenantStatus status = TenantStatus.Active,
        string[]? roles = null,
        bool userActive = true,
        bool mfaSatisfied = true,
        DateTimeOffset? revokedAt = null,
        DateTimeOffset? lastSeenAt = null,
        DateTimeOffset? absoluteExpiry = null,
        int? idleTimeoutMinutes = null)
        => new()
        {
            SessionId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            UserId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            TenantId = 1,
            CompanyId = 1,
            TenantStatus = status,
            Roles = roles ?? [BuiltInRoles.CoreAdmin],
            UserActive = userActive,
            MfaSatisfied = mfaSatisfied,
            RevokedAt = revokedAt,
            LastSeenAt = lastSeenAt ?? Now,
            AbsoluteExpiry = absoluteExpiry ?? Now.AddDays(7),
            IdleTimeoutMinutes = idleTimeoutMinutes,
            EmployeeId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            LicensedApps = ["tickets"],
        };
}
