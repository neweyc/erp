using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Data;
using AppPlatform.Core.Features.Catalogs;
using AppPlatform.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Core.Features.Employees;

public static class GetEmployeesFeature
{
    /// <param name="Department">Null when the employee has none; likewise the job title and location.</param>
    public record EmployeeModel(
        string EmployeeId, string FirstName, string LastName, string DisplayName,
        string? Email, string Status,
        PlacementModel? Department, PlacementModel? JobTitle, PlacementModel? Location);

    /// <param name="Name">For a department, its whole path ("Engineering › Platform").</param>
    /// <param name="Active">False when retired: the employee keeps it, and the edit form must still show it.</param>
    public record PlacementModel(string Id, string Name, bool Active);

    public class GetEmployeesQueryHandler(IEmployeeService employees, ICatalogService catalogs)
    {
        public async Task<CommandResult> Handle(
            Caller caller, bool includeTerminated, CancellationToken ct = default)
        {
            var rows = await employees.ListAsync(includeTerminated, ct);

            // Three small lists, read once, rather than a join per employee.
            var departments = await catalogs.ListDepartmentsAsync(ct);
            var paths = DepartmentRules.Paths(departments);
            var departmentById = departments.ToDictionary(
                d => d.Id, d => new PlacementModel(d.PublicId, paths.GetValueOrDefault(d.Id, d.Name), d.Active));
            var jobTitleById = (await catalogs.ListJobTitlesAsync(ct)).ToDictionary(
                t => t.Id, t => new PlacementModel(t.PublicId, t.Name, t.Active));
            var locationById = (await catalogs.ListLocationsAsync(ct)).ToDictionary(
                l => l.Id, l => new PlacementModel(l.PublicId, l.Name, l.Active));

            return CommandResult.Ok(rows.Select(e => new EmployeeModel(
                e.PublicId, e.FirstName, e.LastName, e.DisplayName, e.Email,
                e.Status.ToString(),
                e.DepartmentId is { } d ? departmentById[d] : null,
                e.JobTitleId is { } t ? jobTitleById[t] : null,
                e.LocationId is { } l ? locationById[l] : null)).ToList());
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapGet("/api/core/v1/employees",
            async (
                [FromServices] ICallerContext callerContext,
                [FromServices] IEmployeeService employees,
                [FromServices] ICatalogService catalogs,
                CancellationToken ct,
                // Read from the query string and defaulted, so the parameter is genuinely
                // honoured rather than accepted and ignored.
                bool? includeTerminated = null) =>
            {
                var handler = new GetEmployeesQueryHandler(employees, catalogs);
                var result = await handler.Handle(
                    callerContext.Require(), includeTerminated ?? false, ct);
                return result.CreateIResult();
            })
            .RequireAuthorization()
            .RequirePermission(Permissions.Core.EmployeeRead);
    }
}
