using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Data;
using AppPlatform.Core.Features.Roles;
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
    /// <param name="Roles">Role keys to give the account, built-in or custom. Defaults to <c>core:member</c>.</param>
    public record InviteEmployeeCommand(List<string?>? Roles);

    public class InviteEmployeeCommandHandler(
        IEmployeeService employees,
        IUserService users,
        IRoleService roles,
        IOutbox outbox,
        TimeProvider clock)
    {
        public async Task<CommandResult> Handle(
            Caller caller, string employeePublicId, InviteEmployeeCommand cmd,
            CancellationToken ct = default)
        {
            // The prefix is the point: without this check a ticket id passed here finds no
            // employee, the caller gets a 404, and the actual mistake — two kinds of id sharing
            // one route parameter — never surfaces.
            if (!PublicId.TryParse(employeePublicId, "emp", out _))
                return CommandResult.NotFound(CoreProblems.NotFound, "That is not an employee id.");

            if (RoleRules.CleanList(cmd.Roles ?? [BuiltInRoles.CoreMember]) is not { } cleaned)
                return CommandResult.Invalid(CoreProblems.ValidationFailed, "Role keys cannot be empty.");
            var requested = cleaned.Order(StringComparer.Ordinal).ToArray();

            // Giving roles is a role write, so it takes the tenant's role-change lock like the
            // others: a custom role named here cannot be deleted before this account is saved
            // holding it (docs/permissions.md §6).
            await using var transaction = await users.BeginTransactionAsync(ct);
            await users.LockRoleChangesAsync(ct);

            var customRoles = RoleRules.ByKey(await roles.ListAsync(ct));
            foreach (var key in requested)
            {
                // The same rule as changing roles later, so an invitation cannot grant what the
                // Users page would refuse.
                if (RoleRules.CannotAssign(key, customRoles, caller.LicensedApps) is { } problem)
                    return CommandResult.Invalid(CoreProblems.ValidationFailed, problem);
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
                Roles = requested,
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
            var message = outbox.AddMessage(
                OutboxTransports.Email,
                destination: user.Email,
                payload: InvitationToken.EmailPayload(user.PublicId, plaintext));
            token.MessageId = message.Id;

            await employees.SaveAsync(ct);
            await transaction.CommitAsync(ct);

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
                [FromServices] IRoleService roles,
                [FromServices] IOutbox outbox,
                [FromServices] TimeProvider clock,
                CancellationToken ct) =>
            {
                var handler = new InviteEmployeeCommandHandler(employees, users, roles, outbox, clock);
                var result = await handler.Handle(callerContext.Require(), employeeId, cmd, ct);
                return result.CreateIResult();
            })
            .RequireAuthorization()
            // core.user.manage, the same permission that changes roles: an invitation grants any
            // role, administrator included, to whoever holds the address. Open to anyone else, a member
            // could create an employee at an address they control, invite it as admin, and accept.
            .RequirePermission(Permissions.Core.UserManage);
    }
}
