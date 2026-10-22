using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Data;
using AppPlatform.Core.Services;
using AppPlatform.Ids;
using AppPlatform.Outbox;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Core.Features.Catalogs;

/// <summary>Adds a department, at the top or inside another. The tree's rules: DepartmentRules.</summary>
public static class CreateDepartmentFeature
{
    /// <param name="ParentId">The parent's <c>dept_…</c> id, or null for a top-level department.</param>
    public record CreateDepartmentCommand(string? Name, string? ParentId);

    public class CreateDepartmentCommandHandler(ICatalogService catalogs, IEmployeeService employees, IOutbox outbox)
    {
        public async Task<CommandResult> Handle(Caller caller, CreateDepartmentCommand cmd, CancellationToken ct = default)
        {
            var (name, nameProblem) = CatalogRules.CleanName(cmd.Name, Department.NameMaxLength);
            if (name is null)
                return CommandResult.Invalid(CoreProblems.ValidationFailed, nameProblem!);

            await using var transaction = await catalogs.BeginTransactionAsync(ct);
            await catalogs.LockCatalogsAsync(ct);

            var all = await catalogs.ListDepartmentsAsync(ct);

            // Resolved from this tenant's own list: an id from the request is never trusted as-is.
            Department? parent = null;
            if (!string.IsNullOrWhiteSpace(cmd.ParentId))
            {
                parent = all.FirstOrDefault(d => d.PublicId == cmd.ParentId.Trim());
                if (parent is null)
                    return CommandResult.Invalid(CoreProblems.ValidationFailed, "No such parent department.");
            }

            if (DepartmentRules.Problem(department: null, parent, active: true, all) is { } problem)
                return CommandResult.Invalid(CoreProblems.ValidationFailed, problem);

            if (DepartmentRules.NameTaken(department: null, name, parent, all))
                return CommandResult.Conflict(CoreProblems.CatalogNameTaken, DepartmentRules.NameTakenMessage(parent));

            var department = new Department
            {
                PublicId = PublicId.New("dept").ToString(),
                // A sub-department belongs to its parent's company; a top-level one to the tenant's.
                CompanyId = parent?.CompanyId ?? await employees.DefaultCompanyIdAsync(ct),
                Name = name,
                ParentId = parent?.Id,
            };
            catalogs.Add(department);

            outbox.AddEvent("department", department.PublicId, department.Version, "department.created",
                $$"""{"departmentId":"{{department.PublicId}}"}""");

            await catalogs.SaveAsync(ct);
            await transaction.CommitAsync(ct);

            return CommandResult.Ok(new { departmentId = department.PublicId });
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapPost("/api/core/v1/departments",
            async (
                CreateDepartmentCommand cmd,
                [FromServices] ICallerContext callerContext,
                [FromServices] ICatalogService catalogs,
                [FromServices] IEmployeeService employees,
                [FromServices] IOutbox outbox,
                CancellationToken ct) =>
            {
                var handler = new CreateDepartmentCommandHandler(catalogs, employees, outbox);
                return (await handler.Handle(callerContext.Require(), cmd, ct)).CreateIResult();
            })
            .RequireAuthorization()
            .RequirePermission(Permissions.Core.CatalogManage);
    }
}
