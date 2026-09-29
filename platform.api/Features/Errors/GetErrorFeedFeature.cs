using AppPlatform.Api;
using AppPlatform.Platform.Auth;
using AppPlatform.Platform.Services;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Platform.Features.Errors;

/// <summary>
/// The operator error feed: recent Error-level occurrences across every service, as metadata only
/// (reference, fingerprint, app, tenant, count, time). A customer's quoted reference is found here;
/// its detail is in that service's log under the same reference.
/// </summary>
public static class GetErrorFeedFeature
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;

    public class GetErrorFeedQueryHandler(IErrorFeedService feed)
    {
        public async Task<CommandResult> Handle(int? limit, CancellationToken ct = default)
        {
            // Clamped, not refused: asking for too many is not an error worth stopping for.
            var rows = await feed.RecentAsync(Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit), ct);
            return CommandResult.Ok(rows);
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapGet("/api/platform/v1/errors",
            async (
                [FromServices] IOperatorContext operatorContext,
                [FromServices] IErrorFeedService feed,
                CancellationToken ct,
                int? limit = null) =>
            {
                // Operators only: the operator middleware has already refused anything else; this
                // makes a wiring fault fail loudly instead of serving the feed to nobody in particular.
                operatorContext.Require();
                return (await new GetErrorFeedQueryHandler(feed).Handle(limit, ct)).CreateIResult();
            })
            .RequireAuthorization();
    }
}
