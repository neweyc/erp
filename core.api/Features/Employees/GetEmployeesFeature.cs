using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Data;
using AppPlatform.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Core.Features.Employees;

public static class GetEmployeesFeature
{
    public record EmployeeModel(
        string EmployeeId, string FirstName, string LastName, string DisplayName,
        string? Email, string Status);

    public class GetEmployeesQueryHandler(IEmployeeService employees)
    {
        public async Task<CommandResult> Handle(
            Caller caller, bool includeTerminated, CancellationToken ct = default)
        {
            var rows = await employees.ListAsync(includeTerminated, ct);

            return CommandResult.Ok(rows.Select(e => new EmployeeModel(
                e.PublicId, e.FirstName, e.LastName, e.DisplayName, e.Email,
                e.Status.ToString())).ToList());
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapGet("/api/core/v1/employees",
            async (
                [FromServices] ICallerContext callerContext,
                [FromServices] IEmployeeService employees,
                CancellationToken ct,
                // Read from the query string and defaulted, so the parameter is genuinely
                // honoured rather than accepted and ignored.
                bool? includeTerminated = null) =>
            {
                var handler = new GetEmployeesQueryHandler(employees);
                var result = await handler.Handle(
                    callerContext.Require(), includeTerminated ?? false, ct);
                return result.CreateIResult();
            })
            .RequireAuthorization();
    }
}
