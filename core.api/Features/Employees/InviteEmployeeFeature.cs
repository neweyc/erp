using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Data;
using AppPlatform.Core.Services;
using AppPlatform.Ids;
using AppPlatform.Outbox;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Core.Features.Employees;

/// <summary>
/// "Invite this employee", linking by employee ID.
///
/// This is the NORMAL onboarding path — create the employee today, grant access tomorrow — and
/// it exists so that refusing to link on a bare email address is a reasonable rule rather than
/// a dead end. Matching identity by email address guesses; an id does not. Never ship the
/// rejection without this operation.
/// </summary>
public static class InviteEmployeeFeature
{
    public record InviteEmployeeCommand(string? Role);

    /// <summary>
    /// Who may invite. Administrators only, because an invitation grants any role — admin
    /// included — to whoever holds the email address. Open to every signed-in user, a member could
    /// create an employee at an address they control, invite it as admin, and accept. Declared
    /// here because packages/auth has no role or policy model yet (CLAUDE.md: Roles.* and Policy*
    /// constants, not built); move it there when that exists.
    /// </summary>
    public static readonly string[] RolesThatMayInvite = ["admin"];

    public class InviteEmployeeCommandHandler(
        IEmployeeService employees,
        IUserService users,
        IOutbox outbox,
        TimeProvider clock)
    {
        private static readonly string[] Roles = ["admin", "manager", "member"];

        public async Task<CommandResult> Handle(
            Caller caller, string employeePublicId, InviteEmployeeCommand cmd,
            CancellationToken ct = default)
        {
            // First, before any lookup or write: a refusal must not reveal whether the employee
            // exists, and must stage nothing.
            if (!RolesThatMayInvite.Contains(caller.Role))
            {
                return CommandResult.Forbidden(
                    CoreProblems.NotPermitted, "Only an administrator can invite people.");
            }

            // The prefix is the point: without this check a ticket id passed here finds no
            // employee, the caller gets a 404, and the actual mistake — two kinds of id sharing
            // one route parameter — never surfaces.
            if (!PublicId.TryParse(employeePublicId, "emp", out _))
                return CommandResult.NotFound(CoreProblems.NotFound, "That is not an employee id.");

            var role = cmd.Role?.Trim().ToLowerInvariant();
            if (role is null || !Roles.Contains(role))
            {
                // Names the valid values, which beats a bare framework 400 the caller has to
                // guess at.
                return CommandResult.Invalid(
                    CoreProblems.ValidationFailed,
                    $"Role must be one of: {string.Join(", ", Roles)}.");
            }

            var employee = await employees.FindByPublicIdAsync(employeePublicId, ct);
            if (employee is null)
                return CommandResult.NotFound(CoreProblems.NotFound, "No such employee.");

            if (string.IsNullOrWhiteSpace(employee.Email))
            {
                return CommandResult.Invalid(
                    CoreProblems.ValidationFailed,
                    "This employee has no email address, so there is nowhere to send an invitation.");
            }

            // A terminated employee cannot be given access. This is also why the email-matching
            // rejection has an exception for them: there is no account to point at.
            if (employee.Status == EmployeeStatus.Terminated)
            {
                return CommandResult.Invalid(
                    CoreProblems.EmployeeTerminated,
                    "This employee is terminated and cannot be given access.");
            }

            if (await users.FindByEmployeeAsync(employee.Id, ct) is not null)
            {
                return CommandResult.Conflict(
                    CoreProblems.EmployeeHasAccount, "This employee already has an account.");
            }

            if (await users.EmailInUseAsync(employee.Email, ct))
            {
                return CommandResult.Conflict(
                    CoreProblems.EmailInUse, "An account already exists at that email address.");
            }

            var user = new User
            {
                PublicId = PublicId.New("usr").ToString(),
                CompanyId = employee.CompanyId,
                Email = employee.Email,
                Role = role,
                Status = UserStatus.Invited,
                EmployeeId = employee.Id,
            };

            users.Add(user);

            // The link the invitee accepts with. Without it the account can never be activated:
            // accepting requires a token, and a second invitation is refused because this account
            // now exists (D17 — what this line was missing).
            var (token, plaintext) = InvitationToken.Issue(user.Id, clock.GetUtcNow());
            users.AddToken(token);

            // Staged, then committed with the user and the token — never sent first. Sending before
            // the commit is how a recipient ends up holding a link to an invitation that does not
            // exist; committing before queueing is how an invite is created that nobody is ever
            // told about. All three rows go in one transaction.
            outbox.AddMessage(
                OutboxTransports.Email,
                destination: user.Email,
                payload: $$"""{"kind":"invite","userId":"{{user.PublicId}}","token":"{{plaintext}}"}""");

            await employees.SaveAsync(ct);

            return CommandResult.Ok(new { userId = user.PublicId, email = user.Email });
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapPost(
            "/api/core/v1/employees/{employeeId}/invite",
            async (
                string employeeId,
                InviteEmployeeCommand cmd,
                [FromServices] ICallerContext callerContext,
                [FromServices] IEmployeeService employees,
                [FromServices] IUserService users,
                [FromServices] IOutbox outbox,
                [FromServices] TimeProvider clock,
                CancellationToken ct) =>
            {
                var handler = new InviteEmployeeCommandHandler(employees, users, outbox, clock);
                var result = await handler.Handle(callerContext.Require(), employeeId, cmd, ct);
                return result.CreateIResult();
            })
            .RequireAuthorization();
    }
}
