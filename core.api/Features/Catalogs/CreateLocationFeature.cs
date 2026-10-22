using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Data;
using AppPlatform.Core.Services;
using AppPlatform.Ids;
using AppPlatform.Outbox;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Core.Features.Catalogs;

/// <summary>Adds a location. Names are unique in the tenant ignoring case, inactive ones included.</summary>
public static class CreateLocationFeature
{
    public record CreateLocationCommand(string? Name);

    public class CreateLocationCommandHandler(ICatalogService catalogs, IEmployeeService employees, IOutbox outbox)
    {
        public async Task<CommandResult> Handle(Caller caller, CreateLocationCommand cmd, CancellationToken ct = default)
        {
            var (name, problem) = CatalogRules.CleanName(cmd.Name, Location.NameMaxLength);
            if (name is null)
                return CommandResult.Invalid(CoreProblems.ValidationFailed, problem!);

            // Under the catalog lock, so two adds of the same name cannot both pass the check.
            await using var transaction = await catalogs.BeginTransactionAsync(ct);
            await catalogs.LockCatalogsAsync(ct);

            // A retired one counts too: reactivating it is the way back, not a second row.
            if ((await catalogs.ListLocationsAsync(ct)).Any(x => CatalogRules.SameName(x.Name, name)))
                return CommandResult.Conflict(CoreProblems.CatalogNameTaken, "Another location already has that name.");

            var location = new Location
            {
                PublicId = PublicId.New("loc").ToString(),
                CompanyId = await employees.DefaultCompanyIdAsync(ct),
                Name = name,
            };
            catalogs.Add(location);

            outbox.AddEvent("location", location.PublicId, location.Version, "location.created",
                $$"""{"locationId":"{{location.PublicId}}"}""");

            await catalogs.SaveAsync(ct);
            await transaction.CommitAsync(ct);

            return CommandResult.Ok(new { locationId = location.PublicId });
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapPost("/api/core/v1/locations",
            async (
                CreateLocationCommand cmd,
                [FromServices] ICallerContext callerContext,
                [FromServices] ICatalogService catalogs,
                [FromServices] IEmployeeService employees,
                [FromServices] IOutbox outbox,
                CancellationToken ct) =>
            {
                var handler = new CreateLocationCommandHandler(catalogs, employees, outbox);
                return (await handler.Handle(callerContext.Require(), cmd, ct)).CreateIResult();
            })
            .RequireAuthorization()
            .RequirePermission(Permissions.Core.CatalogManage);
    }
}
