using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Data;
using AppPlatform.Core.Services;
using AppPlatform.Ids;
using AppPlatform.Outbox;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Core.Features.Catalogs;

/// <summary>Adds a job title. Names are unique in the tenant ignoring case, inactive ones included.</summary>
public static class CreateJobTitleFeature
{
    public record CreateJobTitleCommand(string? Name);

    public class CreateJobTitleCommandHandler(ICatalogService catalogs, IOutbox outbox)
    {
        public async Task<CommandResult> Handle(Caller caller, CreateJobTitleCommand cmd, CancellationToken ct = default)
        {
            var (name, problem) = CatalogRules.CleanName(cmd.Name, JobTitle.NameMaxLength);
            if (name is null)
                return CommandResult.Invalid(CoreProblems.ValidationFailed, problem!);

            // Under the catalog lock, so two adds of the same name cannot both pass the check.
            await using var transaction = await catalogs.BeginTransactionAsync(ct);
            await catalogs.LockCatalogsAsync(ct);

            // A retired one counts too: reactivating it is the way back, not a second row.
            if ((await catalogs.ListJobTitlesAsync(ct)).Any(x => CatalogRules.SameName(x.Name, name)))
                return CommandResult.Conflict(CoreProblems.CatalogNameTaken, "Another job title already has that name.");

            var jobTitle = new JobTitle
            {
                PublicId = PublicId.New("title").ToString(),
                Name = name,
            };
            catalogs.Add(jobTitle);

            outbox.AddEvent("job_title", jobTitle.PublicId, jobTitle.Version, "job_title.created",
                $$"""{"jobTitleId":"{{jobTitle.PublicId}}"}""");

            await catalogs.SaveAsync(ct);
            await transaction.CommitAsync(ct);

            return CommandResult.Ok(new { jobTitleId = jobTitle.PublicId });
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapPost("/api/core/v1/job-titles",
            async (
                CreateJobTitleCommand cmd,
                [FromServices] ICallerContext callerContext,
                [FromServices] ICatalogService catalogs,
                [FromServices] IOutbox outbox,
                CancellationToken ct) =>
            {
                var handler = new CreateJobTitleCommandHandler(catalogs, outbox);
                return (await handler.Handle(callerContext.Require(), cmd, ct)).CreateIResult();
            })
            .RequireAuthorization()
            .RequirePermission(Permissions.Core.CatalogManage);
    }
}
