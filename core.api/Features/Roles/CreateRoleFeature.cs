using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Data;
using AppPlatform.Core.Services;
using AppPlatform.Ids;
using AppPlatform.Outbox;
using Microsoft.AspNetCore.Mvc;
using static AppPlatform.Core.Features.Roles.RoleRules;

namespace AppPlatform.Core.Features.Roles;

/// <summary>Defines one of the tenant's own roles. Design: docs/permissions.md §6.</summary>
public static class CreateRoleFeature
{
    public class CreateRoleCommandHandler(IUserService users, IRoleService roles, IOutbox outbox)
    {
        public async Task<CommandResult> Handle(Caller caller, RoleDefinitionCommand cmd, CancellationToken ct = default)
        {
            // Under the tenant's role-change lock like every role write, so the name check below
            // cannot race another create.
            await using var transaction = await users.BeginTransactionAsync(ct);
            await users.LockRoleChangesAsync(ct);

            var (problem, definition) = Validate(cmd, existing: null, await roles.ListAsync(ct), caller.LicensedApps);
            if (problem is not null) return problem;

            var role = new Role
            {
                PublicId = PublicId.New("rol").ToString(),
                Name = definition!.Name,
                Description = definition.Description,
                Permissions = definition.Permissions,
            };
            roles.Add(role);

            outbox.AddEvent("role", role.PublicId, role.Version, "role.created",
                $$"""{"roleId":"{{role.PublicId}}"}""");

            await users.SaveAsync(ct);
            await transaction.CommitAsync(ct);

            return CommandResult.Ok(new { roleId = role.PublicId, role.Name, role.Description, role.Permissions });
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapPost("/api/core/v1/roles",
            async (
                RoleDefinitionCommand cmd,
                [FromServices] ICallerContext callerContext,
                [FromServices] IUserService users,
                [FromServices] IRoleService roles,
                [FromServices] IOutbox outbox,
                CancellationToken ct) =>
            {
                var handler = new CreateRoleCommandHandler(users, roles, outbox);
                return (await handler.Handle(callerContext.Require(), cmd, ct)).CreateIResult();
            })
            .RequireAuthorization()
            .RequirePermission(Permissions.Core.RoleManage);
    }
}
