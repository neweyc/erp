using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Services;
using AppPlatform.Ids;
using AppPlatform.Outbox;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Core.Features.Roles;

/// <summary>
/// Deletes one of the tenant's own roles, but only once nobody holds it. Deleting a held role
/// would change those people's access silently; the administrator removes it from them first,
/// on the Users page, where each change is visible and checked.
/// </summary>
public static class DeleteRoleFeature
{
    public class DeleteRoleCommandHandler(IUserService users, IRoleService roles, IOutbox outbox)
    {
        public async Task<CommandResult> Handle(Caller caller, string rolePublicId, CancellationToken ct = default)
        {
            if (!PublicId.TryParse(rolePublicId, "rol", out _))
                return CommandResult.NotFound(CoreProblems.NotFound, "That is not one of your organization's roles.");

            // Under the lock, so nobody can be given the role between the check and the delete.
            await using var transaction = await users.BeginTransactionAsync(ct);
            await users.LockRoleChangesAsync(ct);

            var role = await roles.FindByPublicIdAsync(rolePublicId, ct);
            if (role is null)
                return CommandResult.NotFound(CoreProblems.NotFound, "No such role.");

            var holders = (await users.ListAsync(ct)).Count(u => u.Roles.Contains(role.PublicId));
            if (holders > 0)
            {
                return CommandResult.Conflict(
                    CoreProblems.RoleInUse,
                    holders == 1
                        ? "One person holds this role. Remove it from them on the Users page first."
                        : $"{holders} people hold this role. Remove it from them on the Users page first.");
            }

            roles.Remove(role);
            outbox.AddEvent("role", role.PublicId, role.Version + 1, "role.deleted",
                $$"""{"roleId":"{{role.PublicId}}"}""");

            await users.SaveAsync(ct);
            await transaction.CommitAsync(ct);

            return CommandResult.Ok(new { roleId = role.PublicId });
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapDelete("/api/core/v1/roles/{roleId}",
            async (
                string roleId,
                [FromServices] ICallerContext callerContext,
                [FromServices] IUserService users,
                [FromServices] IRoleService roles,
                [FromServices] IOutbox outbox,
                CancellationToken ct) =>
            {
                var handler = new DeleteRoleCommandHandler(users, roles, outbox);
                return (await handler.Handle(callerContext.Require(), roleId, ct)).CreateIResult();
            })
            .RequireAuthorization()
            .RequirePermission(Permissions.Core.RoleManage);
    }
}
