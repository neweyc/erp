using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Data;
using AppPlatform.Core.Features.Roles;
using AppPlatform.Core.Services;
using AppPlatform.Ids;
using AppPlatform.Outbox;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Core.Features.Users;

/// <summary>
/// Stops an account signing in, at once, without deleting it. Design: docs/permissions.md §6.
///
/// The account keeps its roles, so reactivating it restores exactly what it had. Its sessions are
/// revoked as well as refused: session revalidation already refuses an inactive user, but a revoked
/// session stays dead if the account is later reactivated, so a cookie copied before deactivation
/// is never good again. An invitation not yet accepted stops working too (AcceptInviteFeature
/// accepts only an invited account).
/// </summary>
public static class DeactivateUserFeature
{
    public class DeactivateUserCommandHandler(IUserService users, IRoleService roles, IOutbox outbox, TimeProvider clock)
    {
        public async Task<CommandResult> Handle(Caller caller, string userPublicId, CancellationToken ct = default)
        {
            if (!PublicId.TryParse(userPublicId, "usr", out _))
                return CommandResult.NotFound(CoreProblems.NotFound, "That is not a user id.");

            // Deactivation takes access away, so it takes the tenant's role-change lock first, like
            // every role write: the last-administrator check below then reads a set that no
            // concurrent change can alter before this commits.
            await using var transaction = await users.BeginTransactionAsync(ct);
            await users.LockRoleChangesAsync(ct);

            var user = await users.FindByPublicIdAsync(userPublicId, ct);
            if (user is null)
                return CommandResult.NotFound(CoreProblems.NotFound, "No such user.");

            // Signing yourself out of the organisation by accident is easy and hard to undo.
            // Another administrator can do it deliberately.
            if (user.Id == caller.UserId)
            {
                return CommandResult.Invalid(
                    CoreProblems.CannotDeactivateSelf, "You cannot deactivate your own account. Ask another administrator.");
            }

            // Already done: the outcome asked for is true, and saving again would emit an event for
            // a change that never happened.
            if (user.Status == UserStatus.Deactivated)
                return CommandResult.Ok(ToModel(user));

            // An inactive user counts for nothing in the check, which is what holding no roles
            // models here.
            var customRoles = RoleRules.ByKey(await roles.ListAsync(ct));
            var everyone = await users.ListAsync(ct);
            if (RoleRules.RemovesLastAdmin(everyone, customRoles, u => u.Id == user.Id ? [] : u.Roles, customRoles))
                return RoleRules.LastAdmin();

            user.Status = UserStatus.Deactivated;
            user.AccessVersion++;

            var now = clock.GetUtcNow();
            foreach (var session in await users.ListLiveSessionsAsync(user.Id, ct))
                session.RevokedAt = now;

            outbox.AddEvent("user", user.PublicId, user.AccessVersion, "user.deactivated",
                $$"""{"userId":"{{user.PublicId}}"}""");

            await users.SaveAsync(ct);
            await transaction.CommitAsync(ct);

            return CommandResult.Ok(ToModel(user));
        }

        private static object ToModel(User user) => new { userId = user.PublicId, status = user.Status.ToString() };
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapPost(
            "/api/core/v1/users/{userId}/deactivate",
            async (
                string userId,
                [FromServices] ICallerContext callerContext,
                [FromServices] IUserService users,
                [FromServices] IRoleService roles,
                [FromServices] IOutbox outbox,
                [FromServices] TimeProvider clock,
                CancellationToken ct) =>
            {
                var handler = new DeactivateUserCommandHandler(users, roles, outbox, clock);
                return (await handler.Handle(callerContext.Require(), userId, ct)).CreateIResult();
            })
            .RequireAuthorization()
            .RequirePermission(Permissions.Core.UserManage);
    }
}
