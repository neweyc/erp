using AppPlatform.Api;
using AppPlatform.Auth;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Core.Features.Users;

/// <summary>
/// The roles an administrator can assign: core's, and those of each app the tenant licenses. Served
/// from the catalogue so the Users page has no second copy of role names and descriptions to drift.
/// </summary>
public static class GetRolesFeature
{
    public record RoleModel(string App, string Name, string Description, IReadOnlyList<string> Permissions);

    public class GetRolesQueryHandler
    {
        public CommandResult Handle(Caller caller)
        {
            var roles = RoleCatalogue.All
                .Where(r => r.App == RoleCatalogue.CoreApp || caller.LicensedApps.Contains(r.App))
                .Select(r => new RoleModel(r.App, r.Name, r.Description, r.Permissions))
                .ToList();

            return CommandResult.Ok(roles);
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapGet("/api/core/v1/roles",
            ([FromServices] ICallerContext callerContext) =>
                new GetRolesQueryHandler().Handle(callerContext.Require()).CreateIResult())
            .RequireAuthorization()
            .RequirePermission(Permissions.Core.UserRead);
    }
}
