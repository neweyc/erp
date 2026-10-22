using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Core.Features.Catalogs;

/// <summary>
/// The tenant's departments in tree order, inactive included (a page that edits them needs both,
/// and an employee may still be in a retired one). Readable by anyone who can see the roster, since
/// the roster shows where people sit.
/// </summary>
public static class GetDepartmentsFeature
{
    /// <param name="Depth">1 at the top, so a picker can indent.</param>
    /// <param name="Path">"Engineering › Platform": the name to show wherever siblings elsewhere could share it.</param>
    public record DepartmentModel(string DepartmentId, string Name, string? ParentId, int Depth, string Path, bool Active);

    public class GetDepartmentsQueryHandler(ICatalogService catalogs)
    {
        public async Task<CommandResult> Handle(Caller caller, CancellationToken ct = default)
        {
            var all = await catalogs.ListDepartmentsAsync(ct);
            var publicIds = all.ToDictionary(d => d.Id, d => d.PublicId);

            return CommandResult.Ok(DepartmentRules.InTreeOrder(all)
                .Select(x => new DepartmentModel(
                    x.Department.PublicId,
                    x.Department.Name,
                    x.Department.ParentId is { } parentId ? publicIds[parentId] : null,
                    x.Depth,
                    x.Path,
                    x.Department.Active))
                .ToList());
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapGet("/api/core/v1/departments",
            async ([FromServices] ICallerContext callerContext, [FromServices] ICatalogService catalogs, CancellationToken ct) =>
            {
                var handler = new GetDepartmentsQueryHandler(catalogs);
                return (await handler.Handle(callerContext.Require(), ct)).CreateIResult();
            })
            .RequireAuthorization()
            .RequirePermission(Permissions.Core.EmployeeRead);
    }
}
