using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Data;
using AppPlatform.Core.Services;
using AppPlatform.Ids;
using AppPlatform.Outbox;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Core.Features.Users;

/// <summary>
/// Sends an invited person a fresh link: for an email that never arrived (the outbox gave up on
/// it), or a link that expired. Without this, the only fix was editing the database by hand.
///
/// Safe to repeat. Each call issues a new link and leaves earlier ones alone; whichever is used
/// first activates the account, and every other link is refused from then on, because only an
/// INVITED account can accept (AcceptInviteFeature). Leaving them alone avoids a race with someone
/// accepting an older link at the same moment.
///
/// No domain event: the account itself does not change. The token row and the email are the record.
/// </summary>
public static class ResendInvitationFeature
{
    public class ResendInvitationCommandHandler(
        IUserService users, IEmployeeService employees, IOutbox outbox, TimeProvider clock)
    {
        public async Task<CommandResult> Handle(Caller caller, string userPublicId, CancellationToken ct = default)
        {
            if (!PublicId.TryParse(userPublicId, "usr", out _))
                return CommandResult.NotFound(CoreProblems.NotFound, "That is not a user id.");

            var user = await users.FindByPublicIdAsync(userPublicId, ct);
            if (user is null)
                return CommandResult.NotFound(CoreProblems.NotFound, "No such user.");

            // An active account has a password; a deactivated one is reactivated first. No lock is
            // taken: if the account is deactivated between this check and the save, the link sent
            // is refused when used, because acceptance re-checks the status under the lock.
            if (user.Status != UserStatus.Invited)
            {
                return CommandResult.Conflict(
                    CoreProblems.NotInvited, "Only someone who has not yet accepted an invitation can be sent another.");
            }

            // As in InviteEmployeeFeature: a terminated employee is not given access.
            if (user.EmployeeId is { } employeeId
                && await employees.FindByIdAsync(employeeId, ct) is { Status: EmployeeStatus.Terminated })
            {
                return CommandResult.Invalid(
                    CoreProblems.EmployeeTerminated, "This person's employee record is terminated, so they cannot be invited.");
            }

            // The same issuer and email as the first invitation; staged and committed in one save.
            var (token, plaintext) = InvitationToken.Issue(user.Id, clock.GetUtcNow());
            users.AddToken(token);
            var message = outbox.AddMessage(OutboxTransports.Email, destination: user.Email,
                payload: InvitationToken.EmailPayload(user.PublicId, plaintext));
            token.MessageId = message.Id;

            await users.SaveAsync(ct);

            return CommandResult.Ok(new { userId = user.PublicId, expiresAt = token.ExpiresAt });
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapPost(
            "/api/core/v1/users/{userId}/invitation",
            async (
                string userId,
                [FromServices] ICallerContext callerContext,
                [FromServices] IUserService users,
                [FromServices] IEmployeeService employees,
                [FromServices] IOutbox outbox,
                [FromServices] TimeProvider clock,
                CancellationToken ct) =>
            {
                var handler = new ResendInvitationCommandHandler(users, employees, outbox, clock);
                return (await handler.Handle(callerContext.Require(), userId, ct)).CreateIResult();
            })
            .RequireAuthorization()
            // core.user.manage, as inviting is: the link activates an account holding whatever
            // roles it was given.
            .RequirePermission(Permissions.Core.UserManage);
    }
}
