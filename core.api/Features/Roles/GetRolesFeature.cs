using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Core.Features.Roles;

/// <summary>
/// Every role the tenant can see: the built-in roles for core and its licensed apps, then its own.
/// The Users and Roles pages read names and descriptions from here, so there is no second copy in
/// the shell to drift.
/// </summary>
public static class GetRolesFeature
{
    /// <param name="Key">What <c>identity.user.roles</c> stores: <c>ledger:controller</c> or <c>rol_…</c>.</param>
    /// <param name="Holders">How many accounts hold the role, invited ones included.</param>
    public record RoleModel(
        string Key, string Name, string Description, bool BuiltIn,
        IReadOnlyList<string> Permissions, int Holders);

    public class GetRolesQueryHandler(IUserService users, IRoleService roles)
    {
        public async Task<CommandResult> Handle(Caller caller, CancellationToken ct = default)
        {
            var accounts = await users.ListAsync(ct);
            int HoldersOf(string key) => accounts.Count(u => u.Roles.Contains(key));

            var builtIn = BuiltInRoles.All
                .Where(r => r.App == Permissions.CoreApp || caller.LicensedApps.Contains(r.App))
                .Select(r => new RoleModel(r.Key, r.Name, r.Description, true, r.Permissions, HoldersOf(r.Key)));

            var custom = (await roles.ListAsync(ct))
                .Select(r => new RoleModel(r.PublicId, r.Name, r.Description, false, r.Permissions, HoldersOf(r.PublicId)));

            return CommandResult.Ok(builtIn.Concat(custom).ToList());
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapGet("/api/core/v1/roles",
            async (
                [FromServices] ICallerContext callerContext,
                [FromServices] IUserService users,
                [FromServices] IRoleService roles,
                CancellationToken ct) =>
            {
                var handler = new GetRolesQueryHandler(users, roles);
                return (await handler.Handle(callerContext.Require(), ct)).CreateIResult();
            })
            .RequireAuthorization()
            .RequirePermission(Permissions.Core.UserRead);
    }
}
