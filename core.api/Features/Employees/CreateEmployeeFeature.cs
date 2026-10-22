using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Data;
using AppPlatform.Core.Services;
using AppPlatform.Ids;
using AppPlatform.Outbox;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Core.Features.Employees;

public static class CreateEmployeeFeature
{
    public record CreateEmployeeCommand(string? FirstName, string? LastName, string? Email);

    public class CreateEmployeeCommandHandler(
        IEmployeeService employees,
        IOutbox outbox)
    {
        public async Task<CommandResult> Handle(
            Caller caller, CreateEmployeeCommand cmd, CancellationToken ct = default)
        {
            var (details, problem) = EmployeeRules.Clean(cmd.FirstName, cmd.LastName, cmd.Email);
            if (details is null)
                return CommandResult.Invalid(CoreProblems.ValidationFailed, problem!);

            if (details.Email is not null && await employees.EmailInUseAsync(details.Email, ct))
            {
                return CommandResult.Conflict(
                    CoreProblems.EmailInUse, "Another employee already has that email address.");
            }

            var employee = new Employee
            {
                // Assigned here rather than by the database, because the event emitted below
                // must carry it and both have to be in one transaction.
                PublicId = PublicId.New("emp").ToString(),
                CompanyId = await employees.DefaultCompanyIdAsync(ct),
                FirstName = details.FirstName,
                LastName = details.LastName,
                Email = details.Email,
            };

            employees.Add(employee);

            // Emitted with nothing subscribed. The reason is retrofit cost: adding emission
            // later means revisiting every handler that already exists, and the one that gets
            // missed is silent — no test fails when an event is not raised.
            outbox.AddEvent(
                aggregateType: "employee",
                aggregatePublicId: employee.PublicId,
                aggregateVersion: employee.Version,
                eventType: "employee.created",
                payload: $$"""{"employeeId":"{{employee.PublicId}}"}""");

            // One save: the employee and its event commit together or not at all.
            await employees.SaveAsync(ct);

            return CommandResult.Ok(new { employeeId = employee.PublicId });
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapPost("/api/core/v1/employees",
            async (
                CreateEmployeeCommand cmd,
                [FromServices] ICallerContext callerContext,
                [FromServices] IEmployeeService employees,
                [FromServices] IOutbox outbox,
                CancellationToken ct) =>
            {
                var handler = new CreateEmployeeCommandHandler(employees, outbox);
                var result = await handler.Handle(callerContext.Require(), cmd, ct);
                return result.CreateIResult();
            })
            .RequireAuthorization()
            .RequirePermission(Permissions.Core.EmployeeCreate);
    }
}
