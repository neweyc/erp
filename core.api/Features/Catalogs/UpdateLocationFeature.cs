using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Data;
using AppPlatform.Core.Services;
using AppPlatform.Ids;
using AppPlatform.Outbox;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Core.Features.Catalogs;

/// <summary>
/// Renames, deactivates or reactivates a location. The whole record is sent, so what is saved is
/// exactly what the form showed. Deactivating leaves everyone who has it as they are; it is simply
/// no longer offered. A rename shows on every employee at once, because they hold it by id.
/// </summary>
public static class UpdateLocationFeature
{
    /// <param name="Active">Required: a form that forgot it must not deactivate the location.</param>
    public record UpdateLocationCommand(string? Name, bool? Active);

    public class UpdateLocationCommandHandler(ICatalogService catalogs, IOutbox outbox)
    {
        public async Task<CommandResult> Handle(
            Caller caller, string publicId, UpdateLocationCommand cmd, CancellationToken ct = default)
        {
            if (!PublicId.TryParse(publicId, "loc", out _))
                return CommandResult.NotFound(CoreProblems.NotFound, "That is not a location id.");

            var (name, problem) = CatalogRules.CleanName(cmd.Name, Location.NameMaxLength);
            if (name is null)
                return CommandResult.Invalid(CoreProblems.ValidationFailed, problem!);
            if (cmd.Active is not { } active)
                return CommandResult.Invalid(CoreProblems.ValidationFailed, "Say whether the location is active.");

            await using var transaction = await catalogs.BeginTransactionAsync(ct);
            await catalogs.LockCatalogsAsync(ct);

            var all = await catalogs.ListLocationsAsync(ct);
            var location = all.FirstOrDefault(x => x.PublicId == publicId);
            if (location is null)
                return CommandResult.NotFound(CoreProblems.NotFound, "No such location.");

            // Nothing changed: no save, and no event for a change that never happened.
            if (location.Name == name && location.Active == active)
                return CommandResult.Ok(new { locationId = location.PublicId });

            if (all.Any(x => x.Id != location.Id && CatalogRules.SameName(x.Name, name)))
                return CommandResult.Conflict(CoreProblems.CatalogNameTaken, "Another location already has that name.");

            location.Name = name;
            location.Active = active;
            location.Version++;

            outbox.AddEvent("location", location.PublicId, location.Version, "location.updated",
                $$"""{"locationId":"{{location.PublicId}}"}""");

            await catalogs.SaveAsync(ct);
            await transaction.CommitAsync(ct);

            return CommandResult.Ok(new { locationId = location.PublicId });
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapPut("/api/core/v1/locations/{locationId}",
            async (
                string locationId,
                UpdateLocationCommand cmd,
                [FromServices] ICallerContext callerContext,
                [FromServices] ICatalogService catalogs,
                [FromServices] IOutbox outbox,
                CancellationToken ct) =>
            {
                var handler = new UpdateLocationCommandHandler(catalogs, outbox);
                return (await handler.Handle(callerContext.Require(), locationId, cmd, ct)).CreateIResult();
            })
            .RequireAuthorization()
            .RequirePermission(Permissions.Core.CatalogManage);
    }
}
