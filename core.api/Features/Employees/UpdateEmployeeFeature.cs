using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Services;
using AppPlatform.Ids;
using AppPlatform.Outbox;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Core.Features.Employees;

/// <summary>
/// Corrects an employee's name or email address. The whole record is sent, so what is saved is
/// exactly what the form showed; a blank email removes it.
///
/// It does NOT change the email an account signs in with. A sign-in address is a credential:
/// changing it here would let anyone allowed to edit the roster move someone's sign-in to an
/// address they control. The roster shows when the two differ.
///
/// Takes the tenant's role-change lock, although it changes no access, for two reasons. An employee's
/// address is where an invitation goes, so an edit must not land between an administrator's
/// invitation checking the address and sending to it (InviteEmployeeFeature compares the address
/// it was shown, under the same lock). And termination writes the same employee: unserialized, both
/// would number their event with the same version, and the loser's whole transaction, account
/// deactivation included, would roll back.
/// </summary>
public static class UpdateEmployeeFeature
{
    public record UpdateEmployeeCommand(string? FirstName, string? LastName, string? Email);

    public class UpdateEmployeeCommandHandler(IEmployeeService employees, IUserService users, IOutbox outbox)
    {
        public async Task<CommandResult> Handle(
            Caller caller, string employeePublicId, UpdateEmployeeCommand cmd, CancellationToken ct = default)
        {
            if (!PublicId.TryParse(employeePublicId, "emp", out _))
                return CommandResult.NotFound(CoreProblems.NotFound, "That is not an employee id.");

            var (details, problem) = EmployeeRules.Clean(cmd.FirstName, cmd.LastName, cmd.Email);
            if (details is null)
                return CommandResult.Invalid(CoreProblems.ValidationFailed, problem!);

            await using var transaction = await users.BeginTransactionAsync(ct);
            await users.LockRoleChangesAsync(ct);

            var employee = await employees.FindByPublicIdAsync(employeePublicId, ct);
            if (employee is null)
                return CommandResult.NotFound(CoreProblems.NotFound, "No such employee.");

            // Nothing changed: no save, and no event for a change that never happened.
            if (employee.FirstName == details.FirstName && employee.LastName == details.LastName
                && employee.Email == details.Email)
            {
                return CommandResult.Ok(new { employeeId = employee.PublicId });
            }

            if (details.Email is not null && details.Email != employee.Email
                && await employees.EmailInUseByAnotherAsync(details.Email, employee.Id, ct))
            {
                return CommandResult.Conflict(
                    CoreProblems.EmailInUse, "Another employee already has that email address.");
            }

            employee.FirstName = details.FirstName;
            employee.LastName = details.LastName;
            employee.Email = details.Email;
            employee.Version++;

            outbox.AddEvent("employee", employee.PublicId, employee.Version, "employee.updated",
                $$"""{"employeeId":"{{employee.PublicId}}"}""");

            await employees.SaveAsync(ct);
            await transaction.CommitAsync(ct);

            return CommandResult.Ok(new { employeeId = employee.PublicId });
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapPut(
            "/api/core/v1/employees/{employeeId}",
            async (
                string employeeId,
                UpdateEmployeeCommand cmd,
                [FromServices] ICallerContext callerContext,
                [FromServices] IEmployeeService employees,
                [FromServices] IUserService users,
                [FromServices] IOutbox outbox,
                CancellationToken ct) =>
            {
                var handler = new UpdateEmployeeCommandHandler(employees, users, outbox);
                return (await handler.Handle(callerContext.Require(), employeeId, cmd, ct)).CreateIResult();
            })
            .RequireAuthorization()
            .RequirePermission(Permissions.Core.EmployeeUpdate);
    }
}
