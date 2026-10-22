using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Data;
using AppPlatform.Core.Services;
using AppPlatform.Ids;
using AppPlatform.Outbox;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Core.Features.Catalogs;

/// <summary>
/// Renames, deactivates or reactivates a job title. The whole record is sent, so what is saved is
/// exactly what the form showed. Deactivating leaves everyone who has it as they are; it is simply
/// no longer offered. A rename shows on every employee at once, because they hold it by id.
/// </summary>
public static class UpdateJobTitleFeature
{
    /// <param name="Active">Required: a form that forgot it must not deactivate the job title.</param>
    public record UpdateJobTitleCommand(string? Name, bool? Active);

    public class UpdateJobTitleCommandHandler(ICatalogService catalogs, IOutbox outbox)
    {
        public async Task<CommandResult> Handle(
            Caller caller, string publicId, UpdateJobTitleCommand cmd, CancellationToken ct = default)
        {
            if (!PublicId.TryParse(publicId, "title", out _))
                return CommandResult.NotFound(CoreProblems.NotFound, "That is not a job title id.");

            var (name, problem) = CatalogRules.CleanName(cmd.Name, JobTitle.NameMaxLength);
            if (name is null)
                return CommandResult.Invalid(CoreProblems.ValidationFailed, problem!);
            if (cmd.Active is not { } active)
                return CommandResult.Invalid(CoreProblems.ValidationFailed, "Say whether the job title is active.");

            await using var transaction = await catalogs.BeginTransactionAsync(ct);
            await catalogs.LockCatalogsAsync(ct);

            var all = await catalogs.ListJobTitlesAsync(ct);
            var jobTitle = all.FirstOrDefault(x => x.PublicId == publicId);
            if (jobTitle is null)
                return CommandResult.NotFound(CoreProblems.NotFound, "No such job title.");

            // Nothing changed: no save, and no event for a change that never happened.
            if (jobTitle.Name == name && jobTitle.Active == active)
                return CommandResult.Ok(new { jobTitleId = jobTitle.PublicId });

            if (all.Any(x => x.Id != jobTitle.Id && CatalogRules.SameName(x.Name, name)))
                return CommandResult.Conflict(CoreProblems.CatalogNameTaken, "Another job title already has that name.");

            jobTitle.Name = name;
            jobTitle.Active = active;
            jobTitle.Version++;

            outbox.AddEvent("job_title", jobTitle.PublicId, jobTitle.Version, "job_title.updated",
                $$"""{"jobTitleId":"{{jobTitle.PublicId}}"}""");

            await catalogs.SaveAsync(ct);
            await transaction.CommitAsync(ct);

            return CommandResult.Ok(new { jobTitleId = jobTitle.PublicId });
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapPut("/api/core/v1/job-titles/{jobTitleId}",
            async (
                string jobTitleId,
                UpdateJobTitleCommand cmd,
                [FromServices] ICallerContext callerContext,
                [FromServices] ICatalogService catalogs,
                [FromServices] IOutbox outbox,
                CancellationToken ct) =>
            {
                var handler = new UpdateJobTitleCommandHandler(catalogs, outbox);
                return (await handler.Handle(callerContext.Require(), jobTitleId, cmd, ct)).CreateIResult();
            })
            .RequireAuthorization()
            .RequirePermission(Permissions.Core.CatalogManage);
    }
}
