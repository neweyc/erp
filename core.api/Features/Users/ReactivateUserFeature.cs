using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Data;
using AppPlatform.Core.Services;
using AppPlatform.Ids;
using AppPlatform.Outbox;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Core.Features.Users;

/// <summary>
/// Undoes a deactivation. The account comes back with the roles it had; its old sessions stay
/// revoked, so the person signs in again.
///
/// An account deactivated before it ever accepted its invitation comes back as INVITED, not
/// active: it has no password, so there is nothing it could sign in with. Its unexpired links work
/// again; if they have expired, resend the invitation.
/// </summary>
public static class ReactivateUserFeature
{
    public class ReactivateUserCommandHandler(IUserService users, IEmployeeService employees, IOutbox outbox)
    {
        public async Task<CommandResult> Handle(Caller caller, string userPublicId, CancellationToken ct = default)
        {
            if (!PublicId.TryParse(userPublicId, "usr", out _))
                return CommandResult.NotFound(CoreProblems.NotFound, "That is not a user id.");

            // Gives access back, so it is serialized with every other access change in the tenant.
            await using var transaction = await users.BeginTransactionAsync(ct);
            await users.LockRoleChangesAsync(ct);

            var user = await users.FindByPublicIdAsync(userPublicId, ct);
            if (user is null)
                return CommandResult.NotFound(CoreProblems.NotFound, "No such user.");

            if (user.Status != UserStatus.Deactivated)
                return CommandResult.Ok(ToModel(user));

            // Termination will deactivate the linked account (core.api/CLAUDE.md). Reactivating it
            // here would quietly undo the termination, so it is refused, as inviting them is.
            if (user.EmployeeId is { } employeeId
                && await employees.FindByIdAsync(employeeId, ct) is { Status: EmployeeStatus.Terminated })
            {
                return CommandResult.Invalid(
                    CoreProblems.EmployeeTerminated, "This person's employee record is terminated, so the account cannot be reactivated.");
            }

            user.Status = user.PasswordHash is null ? UserStatus.Invited : UserStatus.Active;
            user.AccessVersion++;

            outbox.AddEvent("user", user.PublicId, user.AccessVersion, "user.reactivated",
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
            "/api/core/v1/users/{userId}/reactivate",
            async (
                string userId,
                [FromServices] ICallerContext callerContext,
                [FromServices] IUserService users,
                [FromServices] IEmployeeService employees,
                [FromServices] IOutbox outbox,
                CancellationToken ct) =>
            {
                var handler = new ReactivateUserCommandHandler(users, employees, outbox);
                return (await handler.Handle(callerContext.Require(), userId, ct)).CreateIResult();
            })
            .RequireAuthorization()
            .RequirePermission(Permissions.Core.UserManage);
    }
}
