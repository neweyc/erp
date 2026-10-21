using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Services;
using AppPlatform.Ids;
using AppPlatform.Outbox;
using Microsoft.AspNetCore.Mvc;
using static AppPlatform.Core.Features.Roles.RoleRules;

namespace AppPlatform.Core.Features.Roles;

/// <summary>
/// Edits one of the tenant's own roles: name, description and permissions. Built-in roles cannot
/// be edited; their keys are not <c>rol_…</c> ids, so they never match here.
///
/// Everyone holding the role gets the new permissions on their next request, and their access
/// claim no longer matches, so the shell signs them in again (docs/permissions.md §5).
/// </summary>
public static class UpdateRoleFeature
{
    public class UpdateRoleCommandHandler(IUserService users, IRoleService roles, IOutbox outbox)
    {
        public async Task<CommandResult> Handle(
            Caller caller, string rolePublicId, RoleDefinitionCommand cmd, CancellationToken ct = default)
        {
            if (!PublicId.TryParse(rolePublicId, "rol", out _))
                return CommandResult.NotFound(CoreProblems.NotFound, "That is not one of your organization's roles.");

            await using var transaction = await users.BeginTransactionAsync(ct);
            await users.LockRoleChangesAsync(ct);

            var all = await roles.ListAsync(ct);
            var role = all.FirstOrDefault(r => r.PublicId == rolePublicId);
            if (role is null)
                return CommandResult.NotFound(CoreProblems.NotFound, "No such role.");

            var (problem, definition) = Validate(cmd, role, all, caller.LicensedApps);
            if (problem is not null) return problem;

            if (definition!.Name == role.Name
                && definition.Description == role.Description
                && definition.Permissions.SequenceEqual(role.Permissions))
            {
                // Saving anyway would emit an event for nothing and sign out everyone holding it.
                return CommandResult.Ok(ToModel(role.PublicId, definition));
            }

            // Taking core.user.manage out of a role is how a tenant could lose its last administrator
            // without anyone's roles changing.
            var before = ByKey(all);
            var after = new Dictionary<string, Data.Role>(before, StringComparer.Ordinal)
            {
                [role.PublicId] = new() { Name = definition.Name, Permissions = definition.Permissions },
            };
            if (RemovesLastAdmin(await users.ListAsync(ct), before, u => u.Roles, after))
                return LastAdmin();

            role.Name = definition.Name;
            role.Description = definition.Description;
            role.Permissions = definition.Permissions;
            role.Version++;

            outbox.AddEvent("role", role.PublicId, role.Version, "role.updated",
                $$"""{"roleId":"{{role.PublicId}}"}""");

            await users.SaveAsync(ct);
            await transaction.CommitAsync(ct);

            return CommandResult.Ok(ToModel(role.PublicId, definition));
        }

        private static object ToModel(string roleId, ValidDefinition d)
            => new { roleId, d.Name, d.Description, d.Permissions };
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapPut("/api/core/v1/roles/{roleId}",
            async (
                string roleId,
                RoleDefinitionCommand cmd,
                [FromServices] ICallerContext callerContext,
                [FromServices] IUserService users,
                [FromServices] IRoleService roles,
                [FromServices] IOutbox outbox,
                CancellationToken ct) =>
            {
                var handler = new UpdateRoleCommandHandler(users, roles, outbox);
                return (await handler.Handle(callerContext.Require(), roleId, cmd, ct)).CreateIResult();
            })
            .RequireAuthorization()
            .RequirePermission(Permissions.Core.RoleManage);
    }
}
