using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Services;
using AppPlatform.Ids;
using AppPlatform.Outbox;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Core.Features.Users;

/// <summary>
/// Changes a user's core role and/or app roles. Design: docs/permissions.md §6.
///
/// The change takes effect on the user's next request: permissions are recomputed from the live row
/// every time, and the changed access stamp ends their session with <c>role_changed</c> so the shell
/// reloads. Nothing here needs to revoke sessions.
/// </summary>
public static class ChangeUserRolesFeature
{
    /// <param name="Role">The new core role, or null to leave it unchanged.</param>
    /// <param name="AppRoles">
    /// Only the apps named here change; null removes that app's role. An app left out keeps its
    /// role, so a page that shows only licensed apps cannot erase a role held in one the tenant has
    /// since unlicensed.
    /// </param>
    public record ChangeUserRolesCommand(string? Role, Dictionary<string, string?>? AppRoles);

    public class ChangeUserRolesCommandHandler(IUserService users, IOutbox outbox)
    {
        public async Task<CommandResult> Handle(
            Caller caller, string userPublicId, ChangeUserRolesCommand cmd, CancellationToken ct = default)
        {
            if (!PublicId.TryParse(userPublicId, "usr", out _))
                return CommandResult.NotFound(CoreProblems.NotFound, "That is not a user id.");

            var requestedRole = cmd.Role?.Trim().ToLowerInvariant();
            if (requestedRole is not null && RoleCatalogue.Find(RoleCatalogue.CoreApp, requestedRole) is null)
                return Invalid($"Role must be one of: {RoleNames(RoleCatalogue.CoreApp)}.");

            var appChanges = cmd.AppRoles ?? [];
            foreach (var (app, role) in appChanges)
            {
                if (app == RoleCatalogue.CoreApp)
                    return Invalid("The core role is set with 'role', not under 'appRoles'.");

                if (!RoleCatalogue.RolesIn(app).Any())
                    return Invalid($"'{app}' is not an app.");

                // Removing a role is always allowed. Granting one is refused while the app is
                // unlicensed: it would grant nothing today and something unexpected on the day the
                // app is licensed, long after whoever chose it has forgotten.
                if (role is null) continue;

                if (RoleCatalogue.Find(app, role) is null)
                    return Invalid($"A {app} role must be one of: {RoleNames(app)}.");

                if (!caller.LicensedApps.Contains(app))
                    return Invalid($"Your organisation has not licensed {app}, so its roles cannot be granted.");
            }

            // One transaction around the lock, the reads, and the save. The tenant's role-change
            // lock comes first, before anything is read, so role changes in a tenant run one after
            // another and each starts from the previous one's result:
            //  - the last-administrator check below still holds when the change commits;
            //  - a second change to the same user applies on top of the first, not over it.
            // Other writes to the user (accepting an invitation, a password rehash) do not take the
            // lock and write other columns, so a role change never makes them fail.
            await using var transaction = await users.BeginTransactionAsync(ct);
            await users.LockRoleChangesAsync(ct);
            var admins = await users.LockActiveAdminsAsync(ct);

            var user = await users.FindByPublicIdAsync(userPublicId, ct);
            if (user is null)
                return CommandResult.NotFound(CoreProblems.NotFound, "No such user.");

            var newRole = requestedRole ?? user.Role;
            var newAppRoles = AppRoles.Decode(user.AppRoles).ToDictionary();
            foreach (var (app, role) in appChanges)
            {
                if (role is null) newAppRoles.Remove(app);
                else newAppRoles[app] = role;
            }

            var encoded = newAppRoles
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => AppRoles.Encode(pair.Key, pair.Value))
                .ToArray();

            // Nothing to do. Saving anyway would advance the version and emit an event recording a
            // change that never happened, and end the user's session for no reason.
            if (newRole == user.Role && encoded.SequenceEqual(user.AppRoles))
                return CommandResult.Ok(ToModel(user.PublicId, newRole, newAppRoles));

            if (user.Role == RoleCatalogue.Admin && newRole != RoleCatalogue.Admin)
            {
                // The tenant must keep an active administrator, or nobody can manage its people
                // again without operator help. Decided on the set read under the lock: two
                // administrators demoting each other at once would otherwise each see the other and
                // both succeed.
                if (admins.Contains(user.Id) && admins.Count == 1)
                {
                    return CommandResult.Conflict(
                        CoreProblems.LastAdmin,
                        "This is the only active administrator. Make someone else an administrator first.");
                }
            }

            user.Role = newRole;
            user.AppRoles = encoded;
            user.AccessVersion++;

            // The audit row records old and new roles; the event tells integrations they changed.
            outbox.AddEvent("user", user.PublicId, user.AccessVersion, "user.roles_changed",
                $$"""{"userId":"{{user.PublicId}}"}""");

            await users.SaveAsync(ct);
            await transaction.CommitAsync(ct);

            return CommandResult.Ok(ToModel(user.PublicId, newRole, newAppRoles));
        }

        private static CommandResult Invalid(string message)
            => CommandResult.Invalid(CoreProblems.ValidationFailed, message);

        private static string RoleNames(string app)
            => string.Join(", ", RoleCatalogue.RolesIn(app).Select(r => r.Name));

        private static object ToModel(string userId, string role, IReadOnlyDictionary<string, string> appRoles)
            => new { userId, role, appRoles };
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapPut(
            "/api/core/v1/users/{userId}/roles",
            async (
                string userId,
                ChangeUserRolesCommand cmd,
                [FromServices] ICallerContext callerContext,
                [FromServices] IUserService users,
                [FromServices] IOutbox outbox,
                CancellationToken ct) =>
            {
                var handler = new ChangeUserRolesCommandHandler(users, outbox);
                return (await handler.Handle(callerContext.Require(), userId, cmd, ct)).CreateIResult();
            })
            .RequireAuthorization()
            .RequirePermission(Permissions.Core.UserManage);
    }
}
