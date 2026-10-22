using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Data;
using AppPlatform.Core.Features.Roles;
using AppPlatform.Core.Features.Users;
using AppPlatform.Core.Services;
using AppPlatform.Ids;
using AppPlatform.Outbox;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Core.Features.Employees;

/// <summary>
/// Records that someone has left, and ends their access in the same save.
///
/// The employee is kept, not deleted: tickets, journal entries and audit rows still name them.
/// Their account, if they have one, is deactivated exactly as the Users page would do it
/// (AccountAccess): sessions revoked at once, roles kept. Reactivating it, or inviting them again,
/// is refused while the employee stays terminated. Undoing a termination is not built yet.
///
/// Takes the tenant's role-change lock first, like every write that can take access away
/// (docs/permissions.md §6): the last-administrator check then reads a set nothing else can change
/// before this commits, and a sign-in or an invitation being accepted at the same moment either
/// finishes first and is revoked, or waits and is refused.
/// </summary>
public static class TerminateEmployeeFeature
{
    public class TerminateEmployeeCommandHandler(
        IEmployeeService employees, IUserService users, IRoleService roles, IOutbox outbox, TimeProvider clock)
    {
        public async Task<CommandResult> Handle(Caller caller, string employeePublicId, CancellationToken ct = default)
        {
            if (!PublicId.TryParse(employeePublicId, "emp", out _))
                return CommandResult.NotFound(CoreProblems.NotFound, "That is not an employee id.");

            await using var transaction = await users.BeginTransactionAsync(ct);
            await users.LockRoleChangesAsync(ct);

            var employee = await employees.FindByPublicIdAsync(employeePublicId, ct);
            if (employee is null)
                return CommandResult.NotFound(CoreProblems.NotFound, "No such employee.");

            // Already done: the outcome asked for is true, and saving again would emit an event for
            // a change that never happened.
            if (employee.Status == EmployeeStatus.Terminated)
                return CommandResult.Ok(ToModel(employee));

            var account = await users.FindByEmployeeAsync(employee.Id, ct);

            // Ending your own access by accident is easy and hard to undo; another administrator can
            // do it deliberately. The same rule as deactivating your own account.
            if (account is not null && account.Id == caller.UserId)
            {
                return CommandResult.Invalid(
                    CoreProblems.CannotTerminateSelf, "You cannot terminate yourself. Ask another administrator.");
            }

            if (account is not null && account.Status != UserStatus.Deactivated)
            {
                var customRoles = RoleRules.ByKey(await roles.ListAsync(ct));
                var everyone = await users.ListAsync(ct);
                if (RoleRules.RemovesLastAdmin(everyone, customRoles, u => u.Id == account.Id ? [] : u.Roles, customRoles))
                    return RoleRules.LastAdmin();

                await AccountAccess.DeactivateAsync(account, users, outbox, clock.GetUtcNow(), ct);
            }

            employee.Status = EmployeeStatus.Terminated;
            employee.Version++;
            outbox.AddEvent("employee", employee.PublicId, employee.Version, "employee.terminated",
                $$"""{"employeeId":"{{employee.PublicId}}"}""");

            // One save for the employee, the account, its sessions and both events.
            await employees.SaveAsync(ct);
            await transaction.CommitAsync(ct);

            return CommandResult.Ok(ToModel(employee));
        }

        private static object ToModel(Employee employee) => new { employeeId = employee.PublicId, status = employee.Status.ToString() };
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapPost(
            "/api/core/v1/employees/{employeeId}/terminate",
            async (
                string employeeId,
                [FromServices] ICallerContext callerContext,
                [FromServices] IEmployeeService employees,
                [FromServices] IUserService users,
                [FromServices] IRoleService roles,
                [FromServices] IOutbox outbox,
                [FromServices] TimeProvider clock,
                CancellationToken ct) =>
            {
                var handler = new TerminateEmployeeCommandHandler(employees, users, roles, outbox, clock);
                return (await handler.Handle(callerContext.Require(), employeeId, ct)).CreateIResult();
            })
            .RequireAuthorization()
            .RequirePermission(Permissions.Core.EmployeeTerminate);
    }
}
