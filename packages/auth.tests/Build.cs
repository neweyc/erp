namespace AppPlatform.Auth.Tests;

internal static class Build
{
    public static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    public static SessionContext Session(
        TenantStatus status = TenantStatus.Active,
        string role = "admin",
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
            Role = role,
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
