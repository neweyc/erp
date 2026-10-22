using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Data;
using AppPlatform.Core.Services;
using AppPlatform.Ids;
using AppPlatform.Outbox;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Core.Features.Catalogs;

/// <summary>
/// Renames, moves, deactivates or reactivates a department. The whole record is sent, so what is
/// saved is exactly what the form showed.
///
/// Deactivating never touches the employees in it: they keep it, and it is no longer offered for
/// new placements. A rename shows on every employee at once, because they hold it by id.
/// </summary>
public static class UpdateDepartmentFeature
{
    /// <param name="ParentId">The parent's <c>dept_…</c> id, or null for top level.</param>
    /// <param name="Active">Required: a form that forgot it must not deactivate the department.</param>
    public record UpdateDepartmentCommand(string? Name, string? ParentId, bool? Active);

    public class UpdateDepartmentCommandHandler(ICatalogService catalogs, IOutbox outbox)
    {
        public async Task<CommandResult> Handle(
            Caller caller, string departmentPublicId, UpdateDepartmentCommand cmd, CancellationToken ct = default)
        {
            if (!PublicId.TryParse(departmentPublicId, "dept", out _))
                return CommandResult.NotFound(CoreProblems.NotFound, "That is not a department id.");

            var (name, nameProblem) = CatalogRules.CleanName(cmd.Name, Department.NameMaxLength);
            if (name is null)
                return CommandResult.Invalid(CoreProblems.ValidationFailed, nameProblem!);
            if (cmd.Active is not { } active)
                return CommandResult.Invalid(CoreProblems.ValidationFailed, "Say whether the department is active.");

            await using var transaction = await catalogs.BeginTransactionAsync(ct);
            await catalogs.LockCatalogsAsync(ct);

            var all = await catalogs.ListDepartmentsAsync(ct);
            var department = all.FirstOrDefault(d => d.PublicId == departmentPublicId);
            if (department is null)
                return CommandResult.NotFound(CoreProblems.NotFound, "No such department.");

            Department? parent = null;
            if (!string.IsNullOrWhiteSpace(cmd.ParentId))
            {
                parent = all.FirstOrDefault(d => d.PublicId == cmd.ParentId.Trim());
                if (parent is null)
                    return CommandResult.Invalid(CoreProblems.ValidationFailed, "No such parent department.");
            }

            // Nothing changed: no save, and no event for a change that never happened.
            if (department.Name == name && department.ParentId == parent?.Id && department.Active == active)
                return CommandResult.Ok(new { departmentId = department.PublicId });

            if (DepartmentRules.Problem(department, parent, active, all) is { } problem)
                return CommandResult.Invalid(CoreProblems.ValidationFailed, problem);

            if (DepartmentRules.NameTaken(department, name, parent, all))
                return CommandResult.Conflict(CoreProblems.CatalogNameTaken, DepartmentRules.NameTakenMessage(parent));

            department.Name = name;
            department.ParentId = parent?.Id;
            department.Active = active;
            department.Version++;

            outbox.AddEvent("department", department.PublicId, department.Version, "department.updated",
                $$"""{"departmentId":"{{department.PublicId}}"}""");

            await catalogs.SaveAsync(ct);
            await transaction.CommitAsync(ct);

            return CommandResult.Ok(new { departmentId = department.PublicId });
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapPut("/api/core/v1/departments/{departmentId}",
            async (
                string departmentId,
                UpdateDepartmentCommand cmd,
                [FromServices] ICallerContext callerContext,
                [FromServices] ICatalogService catalogs,
                [FromServices] IOutbox outbox,
                CancellationToken ct) =>
            {
                var handler = new UpdateDepartmentCommandHandler(catalogs, outbox);
                return (await handler.Handle(callerContext.Require(), departmentId, cmd, ct)).CreateIResult();
            })
            .RequireAuthorization()
            .RequirePermission(Permissions.Core.CatalogManage);
    }
}
