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

    public class InviteEmployeeCommandHandler(
        IEmployeeService employees,
        IUserService users,
        IOutbox outbox)
    {
        private static readonly string[] Roles = ["admin", "manager", "member"];

        public async Task<CommandResult> Handle(
            Caller caller, string employeePublicId, InviteEmployeeCommand cmd,
            CancellationToken ct = default)
        {
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

            // Staged, then committed with the user — never sent first. Sending before the commit
            // is how a recipient ends up holding a link to an invitation that does not exist;
            // committing before queueing is how an invite is created that nobody is ever told
            // about. Both rows go in one transaction.
            outbox.AddMessage(
                OutboxTransports.Email,
                destination: user.Email,
                payload: $$"""{"kind":"invite","userId":"{{user.PublicId}}"}""");

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
                CancellationToken ct) =>
            {
                var handler = new InviteEmployeeCommandHandler(employees, users, outbox);
                var result = await handler.Handle(callerContext.Require(), employeeId, cmd, ct);
                return result.CreateIResult();
            })
            .RequireAuthorization();
    }
}
