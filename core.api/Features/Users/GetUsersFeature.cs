using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Core.Features.Users;

/// <summary>The Users page: every account in the tenant, with its status and roles.</summary>
public static class GetUsersFeature
{
    /// <param name="AppRoles">App name to role name; an app with no entry grants this user nothing in it.</param>
    public record UserModel(
        string UserId, string Email, string Status, string Role, IReadOnlyDictionary<string, string> AppRoles);

    public class GetUsersQueryHandler(IUserService users)
    {
        public async Task<CommandResult> Handle(Caller caller, CancellationToken ct = default)
        {
            var rows = await users.ListAsync(ct);

            return CommandResult.Ok(rows.Select(u => new UserModel(
                u.PublicId, u.Email, u.Status.ToString(), u.Role, AppRoles.Decode(u.AppRoles))).ToList());
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapGet("/api/core/v1/users",
            async (
                [FromServices] ICallerContext callerContext,
                [FromServices] IUserService users,
                CancellationToken ct) =>
            {
                var handler = new GetUsersQueryHandler(users);
                return (await handler.Handle(callerContext.Require(), ct)).CreateIResult();
            })
            .RequireAuthorization()
            .RequirePermission(Permissions.Core.UserRead);
    }
}
