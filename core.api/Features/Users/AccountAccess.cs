using AppPlatform.Core.Data;
using AppPlatform.Core.Services;
using AppPlatform.Outbox;

namespace AppPlatform.Core.Features.Users;

/// <summary>
/// Ending an account's access, shared by deactivating it (DeactivateUserFeature) and terminating
/// its employee (TerminateEmployeeFeature), so the two can never end access differently.
/// </summary>
public static class AccountAccess
{
    /// <summary>
    /// Deactivates the account and revokes its live sessions, staged for the caller's save.
    ///
    /// The caller must already hold the tenant's role-change lock and have run the
    /// last-administrator check: those decide WHETHER this may happen, and this only does it.
    ///
    /// The roles are kept, so reactivating restores exactly what the account had. Sessions are
    /// revoked as well as refused: revalidation already refuses an inactive user, but a revoked
    /// session stays dead after a reactivation, so a cookie copied beforehand is never good again.
    /// </summary>
    public static async Task DeactivateAsync(
        User user, IUserService users, IOutbox outbox, DateTimeOffset now, CancellationToken ct)
    {
        user.Status = UserStatus.Deactivated;
        user.AccessVersion++;

        foreach (var session in await users.ListLiveSessionsAsync(user.Id, ct))
            session.RevokedAt = now;

        outbox.AddEvent("user", user.PublicId, user.AccessVersion, "user.deactivated",
            $$"""{"userId":"{{user.PublicId}}"}""");
    }
}
