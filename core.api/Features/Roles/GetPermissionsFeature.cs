using AppPlatform.Api;
using AppPlatform.Auth;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Core.Features.Roles;

/// <summary>
/// The permissions a role can be built from, with descriptions: core's, and those of each app the
/// tenant licenses. The one source of those descriptions for the role editor.
/// </summary>
public static class GetPermissionsFeature
{
    public record PermissionModel(string Name, string App, string Description);

    public class GetPermissionsQueryHandler
    {
        public CommandResult Handle(Caller caller)
            => CommandResult.Ok(PermissionCatalogue.All
                .Where(p => p.App == Permissions.CoreApp || caller.LicensedApps.Contains(p.App))
                .Select(p => new PermissionModel(p.Name, p.App, p.Description))
                .ToList());
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapGet("/api/core/v1/permissions",
            ([FromServices] ICallerContext callerContext) =>
                new GetPermissionsQueryHandler().Handle(callerContext.Require()).CreateIResult())
            .RequireAuthorization()
            .RequirePermission(Permissions.Core.UserRead);
    }
}
