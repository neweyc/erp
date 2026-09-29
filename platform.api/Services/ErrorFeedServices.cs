using AppPlatform.Platform.Data;
using Microsoft.EntityFrameworkCore;

namespace AppPlatform.Platform.Services;

/// <summary>A feed row as an operator reads it: the tenant by its public id, never its key.</summary>
public record ErrorFeedRow(
    string Reference, string Fingerprint, string App, string? TenantId, int Count, DateTimeOffset OccurredAt);

public interface IErrorFeedService
{
    /// <summary>Newest first. Ties broken by id so paging is stable.</summary>
    Task<List<ErrorFeedRow>> RecentAsync(int limit, CancellationToken ct = default);
}

public class EFErrorFeedService(PlatformDbContext db) : IErrorFeedService
{
    public Task<List<ErrorFeedRow>> RecentAsync(int limit, CancellationToken ct = default)
        => db.ErrorFeed
            .OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id)
            .Take(limit)
            .Select(e => new ErrorFeedRow(
                e.Reference, e.Fingerprint, e.App,
                db.Tenants.Where(t => t.Id == e.TenantId).Select(t => t.PublicId).FirstOrDefault(),
                e.Count, e.OccurredAt))
            .ToListAsync(ct);
}

/// <summary>
/// Keeps the feed bounded: occurrences older than <see cref="Retention"/> are deleted every hour.
/// The feed is for noticing what is failing now; the detail of an old fault is in the service's
/// own logs, found by the reference.
///
/// ExecuteDelete, skipping SaveChanges, is deliberate: these rows are not audited and carry no
/// tenant filter, and loading a month of rows to delete them one by one would be the expensive way
/// to do the same thing.
/// </summary>
public sealed class ErrorFeedPruner(IServiceScopeFactory scopes, TimeProvider clock, ILogger<ErrorFeedPruner> logger)
    : BackgroundService
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                var cutoff = clock.GetUtcNow() - Retention;
                await db.ErrorFeed.Where(e => e.OccurredAt < cutoff).ExecuteDeleteAsync(stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                // A warning, not an error: an error would be reported into the very feed being pruned.
                logger.LogWarning(exception, "Pruning the error feed failed; retrying in an hour.");
            }

            try
            {
                await Task.Delay(Interval, clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
