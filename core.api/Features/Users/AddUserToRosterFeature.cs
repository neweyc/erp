using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Data;
using AppPlatform.Core.Features.Employees;
using AppPlatform.Core.Services;
using AppPlatform.Ids;
using AppPlatform.Outbox;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Core.Features.Users;

/// <summary>
/// Gives an account that has no employee record one, and links the two. Every account belongs to
/// an employee (docs/permissions.md §7); the exception is the administrator made when the tenant is
/// provisioned, who exists before anyone could enter a roster.
///
/// The record is created from the account's own email. If another employee already has that
/// address this refuses rather than linking to them: matching people by email address guesses, and
/// a wrong guess would put someone's access on another person's record.
///
/// Takes the tenant's role-change lock: linking decides whose termination ends this account, so two
/// links made at once must not both succeed.
/// </summary>
public static class AddUserToRosterFeature
{
    public record AddUserToRosterCommand(string? FirstName, string? LastName);

    public class AddUserToRosterCommandHandler(IUserService users, IEmployeeService employees, IOutbox outbox)
    {
        public async Task<CommandResult> Handle(
            Caller caller, string userPublicId, AddUserToRosterCommand cmd, CancellationToken ct = default)
        {
            if (!PublicId.TryParse(userPublicId, "usr", out _))
                return CommandResult.NotFound(CoreProblems.NotFound, "That is not a user id.");

            await using var transaction = await users.BeginTransactionAsync(ct);
            await users.LockRoleChangesAsync(ct);

            var user = await users.FindByPublicIdAsync(userPublicId, ct);
            if (user is null)
                return CommandResult.NotFound(CoreProblems.NotFound, "No such user.");

            if (user.EmployeeId is not null)
                return CommandResult.Conflict(CoreProblems.UserHasEmployee, "This account is already on the roster.");

            var (details, problem) = EmployeeRules.Clean(cmd.FirstName, cmd.LastName, user.Email);
            if (details is null)
                return CommandResult.Invalid(CoreProblems.ValidationFailed, problem!);

            if (await employees.EmailInUseAsync(details.Email!, ct))
            {
                return CommandResult.Conflict(
                    CoreProblems.EmailInUse, "Another employee already has this account's email address.");
            }

            var employee = new Employee
            {
                PublicId = PublicId.New("emp").ToString(),
                CompanyId = user.CompanyId,
                FirstName = details.FirstName,
                LastName = details.LastName,
                Email = details.Email,
            };
            employees.Add(employee);
            user.EmployeeId = employee.Id;
            // Linking is an access change (it decides whose termination ends this account), so it
            // advances the account's access version and the account's own history records it.
            user.AccessVersion++;

            outbox.AddEvent("employee", employee.PublicId, employee.Version, "employee.created",
                $$"""{"employeeId":"{{employee.PublicId}}"}""");
            outbox.AddEvent("user", user.PublicId, user.AccessVersion, "user.employee_linked",
                $$"""{"userId":"{{user.PublicId}}","employeeId":"{{employee.PublicId}}"}""");

            await employees.SaveAsync(ct);
            await transaction.CommitAsync(ct);

            return CommandResult.Ok(new { employeeId = employee.PublicId, userId = user.PublicId });
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapPost(
            "/api/core/v1/users/{userId}/employee",
            async (
                string userId,
                AddUserToRosterCommand cmd,
                [FromServices] ICallerContext callerContext,
                [FromServices] IUserService users,
                [FromServices] IEmployeeService employees,
                [FromServices] IOutbox outbox,
                CancellationToken ct) =>
            {
                var handler = new AddUserToRosterCommandHandler(users, employees, outbox);
                return (await handler.Handle(callerContext.Require(), userId, cmd, ct)).CreateIResult();
            })
            .RequireAuthorization()
            // core.user.manage: linking decides whose termination ends this account.
            .RequirePermission(Permissions.Core.UserManage);
    }
}
