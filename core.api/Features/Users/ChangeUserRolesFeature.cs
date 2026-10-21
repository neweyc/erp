using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Features.Roles;
using AppPlatform.Core.Services;
using AppPlatform.Ids;
using AppPlatform.Outbox;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Core.Features.Users;

/// <summary>
/// Gives roles to a user and takes them away. Design: docs/permissions.md §6.
///
/// The change takes effect on the user's next request: permissions are recomputed from the live
/// rows every time, and the changed access claim ends their session with <c>role_changed</c> so the
/// shell reloads. Nothing here needs to revoke sessions.
/// </summary>
public static class ChangeUserRolesFeature
{
    /// <param name="Add">Role keys to give: built-in (<c>ledger:controller</c>) or custom (<c>rol_…</c>).</param>
    /// <param name="Remove">
    /// Role keys to take away. Additions and removals rather than a replacement list, so two
    /// administrators changing one person at once both get what they asked for, and a page that
    /// does not show a role (one for an app the tenant has since unlicensed) cannot erase it.
    /// </param>
    public record ChangeUserRolesCommand(List<string?>? Add, List<string?>? Remove);

    public class ChangeUserRolesCommandHandler(IUserService users, IRoleService roles, IOutbox outbox)
    {
        public async Task<CommandResult> Handle(
            Caller caller, string userPublicId, ChangeUserRolesCommand cmd, CancellationToken ct = default)
        {
            if (!PublicId.TryParse(userPublicId, "usr", out _))
                return CommandResult.NotFound(CoreProblems.NotFound, "That is not a user id.");

            if (RoleRules.CleanList(cmd.Add) is not { } add || RoleRules.CleanList(cmd.Remove) is not { } remove)
                return Invalid("Role keys cannot be empty.");

            if (add.Count == 0 && remove.Count == 0)
                return Invalid("Name at least one role to add or remove.");

            if (add.Intersect(remove, StringComparer.Ordinal).FirstOrDefault() is { } both)
                return Invalid($"'{both}' cannot be both added and removed.");

            // The tenant's role-change lock comes first, before anything is read, so role writes in
            // a tenant run one after another and each starts from the previous one's result:
            //  - the last-administrator check below still holds when the change commits;
            //  - a role cannot be deleted between being checked here and being saved on the user;
            //  - a second change to the same user applies on top of the first, not over it.
            // Deactivation and accepting an invitation take it too. Other writes to the user (a
            // password rehash) do not, and write other columns, so a role change never makes them fail.
            await using var transaction = await users.BeginTransactionAsync(ct);
            await users.LockRoleChangesAsync(ct);

            var customRoles = RoleRules.ByKey(await roles.ListAsync(ct));
            foreach (var key in add)
            {
                if (RoleRules.CannotAssign(key, customRoles, caller.LicensedApps) is { } problem)
                    return Invalid(problem);
            }

            var user = await users.FindByPublicIdAsync(userPublicId, ct);
            if (user is null)
                return CommandResult.NotFound(CoreProblems.NotFound, "No such user.");

            // Removing a role the user does not hold is a no-op, not an error: the outcome asked
            // for is already true.
            // A NULL element a hand-written row may hold is dropped here rather than written back.
            var newRoles = user.Roles
                .OfType<string>()
                .Except(remove, StringComparer.Ordinal)
                .Union(add, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();

            // Nothing to do. Saving anyway would advance the version and emit an event recording a
            // change that never happened, and end the user's session for no reason.
            if (newRoles.SequenceEqual(user.Roles.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                return CommandResult.Ok(ToModel(user.PublicId, user.Roles));

            // Read under the lock, so no concurrent role write can change who qualifies.
            var everyone = await users.ListAsync(ct);
            if (RoleRules.RemovesLastAdmin(everyone, customRoles, u => u.Id == user.Id ? newRoles : u.Roles, customRoles))
                return RoleRules.LastAdmin();

            user.Roles = newRoles;
            user.AccessVersion++;

            // The audit row records old and new roles; the event tells integrations they changed.
            outbox.AddEvent("user", user.PublicId, user.AccessVersion, "user.roles_changed",
                $$"""{"userId":"{{user.PublicId}}"}""");

            await users.SaveAsync(ct);
            await transaction.CommitAsync(ct);

            return CommandResult.Ok(ToModel(user.PublicId, newRoles));
        }

        private static CommandResult Invalid(string message)
            => CommandResult.Invalid(CoreProblems.ValidationFailed, message);

        private static object ToModel(string userId, IReadOnlyList<string> roles) => new { userId, roles };
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapPatch(
            "/api/core/v1/users/{userId}/roles",
            async (
                string userId,
                ChangeUserRolesCommand cmd,
                [FromServices] ICallerContext callerContext,
                [FromServices] IUserService users,
                [FromServices] IRoleService roles,
                [FromServices] IOutbox outbox,
                CancellationToken ct) =>
            {
                var handler = new ChangeUserRolesCommandHandler(users, roles, outbox);
                return (await handler.Handle(callerContext.Require(), userId, cmd, ct)).CreateIResult();
            })
            .RequireAuthorization()
            .RequirePermission(Permissions.Core.UserManage);
    }
}
