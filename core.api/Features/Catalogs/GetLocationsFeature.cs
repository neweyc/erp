using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Core.Features.Catalogs;

/// <summary>
/// The tenant's locations, inactive included (a page that edits them needs both, and an employee
/// may still hold a retired one). Readable by anyone who can see the roster, since the roster shows them.
/// </summary>
public static class GetLocationsFeature
{
    public record LocationModel(string LocationId, string Name, bool Active);

    public class GetLocationsQueryHandler(ICatalogService catalogs)
    {
        public async Task<CommandResult> Handle(Caller caller, CancellationToken ct = default)
            => CommandResult.Ok((await catalogs.ListLocationsAsync(ct))
                .Select(x => new LocationModel(x.PublicId, x.Name, x.Active))
                .ToList());
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapGet("/api/core/v1/locations",
            async ([FromServices] ICallerContext callerContext, [FromServices] ICatalogService catalogs, CancellationToken ct) =>
            {
                var handler = new GetLocationsQueryHandler(catalogs);
                return (await handler.Handle(callerContext.Require(), ct)).CreateIResult();
            })
            .RequireAuthorization()
            .RequirePermission(Permissions.Core.EmployeeRead);
    }
}
