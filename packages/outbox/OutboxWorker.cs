using AppPlatform.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AppPlatform.Outbox;

public sealed class OutboxWorkerOptions
{
    /// <summary>
    /// The schema this worker serves. It is interpolated into SQL — an identifier cannot be
    /// parameterised — so it is validated here rather than trusted. It comes from configuration
    /// today; validating means the day someone wires it to a request it fails loudly.
    /// </summary>
    public required string Schema
    {
        get;
        init
        {
            SchemaName.Validate(value, nameof(Schema));
            field = value;
        }
    }
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);
    public int BatchSize { get; init; } = 20;
}

/// <summary>
/// Delivers staged outbox messages. One per service, over that service's own schema.
///
/// Generic over the service's DbContext because each service owns its own outbox; a single shared
/// worker would need to reach across schemas, which is the boundary this design exists to keep.
/// </summary>
public sealed class OutboxWorker<TContext>(
    IServiceScopeFactory scopeFactory,
    OutboxWorkerOptions options,
    TimeProvider clock,
    ILogger<OutboxWorker<TContext>> logger) : BackgroundService
    where TContext : DbContext
{
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

    private DateTimeOffset _lastPrunedAt = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainAsync(stoppingToken);
            }
            catch (Exception) when (stoppingToken.IsCancellationRequested)
            {
                // Simply stopping. Checking the token rather than the exception type is safer
                // here: a driver may wrap shutdown cancellation in something else, and at this
                // level the type tells us nothing the token does not.
                return;
            }
            catch (Exception ex)
            {
                // The loop must outlive any single failure. An unhandled exception in a
                // BackgroundService stops the host by default, so a transient database blip
                // would take the whole service down and stop delivering anything.
                logger.LogError(ex, "Outbox sweep failed; retrying after the poll interval.");
            }

            try
            {
                await Task.Delay(options.PollInterval, clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Deletes rows past their retention window.
    ///
    /// Not housekeeping: an invitation payload carries a PLAINTEXT, password-equivalent token,
    /// so an unpruned outbox keeps live credentials in a readable table and in every backup long
    /// after delivery. Retention is the only thing that bounds that exposure.
    ///
    /// Tenant-blind on purpose. This reads no one's data — it removes expired rows across the
    /// schema this service owns — and a per-tenant sweep would need to enumerate tenants to do
    /// the same work.
    /// </summary>
    private async Task PruneAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        var now = clock.GetUtcNow();

        // EF1003 is suppressed, not worked around. A schema is an identifier and cannot be a
        // SQL parameter, so it must be interpolated; the cutoffs, which are the only values that
        // could carry anything hostile, ARE parameters. OutboxWorkerOptions.Schema rejects
        // anything outside ^[a-z_][a-z0-9_]{0,62}$ at construction, so what reaches here has been
        // validated rather than merely trusted.
#pragma warning disable EF1003
        var removed = await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM " + options.Schema + ".outbox_message "
            + "WHERE (status = 'Succeeded' AND completed_at < {0}) "
            + "   OR (status = 'Dead'      AND completed_at < {1})",
            [OutboxRetention.SucceededCutoff(now), OutboxRetention.DeadCutoff(now)], ct);
#pragma warning restore EF1003

        if (removed > 0) logger.LogInformation("Pruned {Removed} outbox message(s).", removed);

        // Events outlive messages: they are the record a subscriber may replay, and they carry
        // no credentials.
#pragma warning disable EF1003
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM " + options.Schema + ".outbox_event WHERE occurred_at < {0}",
            [OutboxRetention.EventCutoff(now)], ct);
#pragma warning restore EF1003
    }

    /// <summary>Exposed so a test can run one sweep deterministically rather than racing a timer.</summary>
    public async Task<int> DrainAsync(CancellationToken ct)
    {
        List<OutboxMessage> claimed;

        // Claimed WITHOUT a tenant scope: a worker serves its whole schema, and the claim is a
        // single statement whose row locking is what makes concurrent workers safe.
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<TContext>();
            var store = new OutboxClaimStore(context, options.Schema);
            claimed = [.. await store.ClaimAsync(options.BatchSize, clock.GetUtcNow(), ct)];
        }

        var delivered = 0;

        foreach (var message in claimed)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (await DeliverAsync(message, ct)) delivered++;
            }
            catch (Exception ex) when (!IsShutdown(ex, ct))
            {
                // One message must not cost the rest of the batch. Anything that escapes
                // DeliverAsync — a failed scope, a failed re-read, a concurrency conflict on
                // save — leaves this row leased and retried when the lease expires, while the
                // messages behind it still go out. Without this, a single bad row blocks every
                // other tenant's mail on every sweep.
                logger.LogError(ex, "Outbox message {MessageId} could not be processed.", message.Id);
            }
        }

        // Pruning runs AFTER delivery and cannot take the batch with it. Placed before, a failed
        // prune — a lock, a statement timeout on a first large sweep — would skip a batch that is
        // already leased, making it invisible until the lease expired.
        //
        // Throttled because retention is measured in days; the timestamp advances only on success
        // so a failure is retried on the next sweep rather than an hour later.
        if (clock.GetUtcNow() - _lastPrunedAt >= PruneInterval)
        {
            try
            {
                await PruneAsync(ct);
                _lastPrunedAt = clock.GetUtcNow();
            }
            catch (Exception ex) when (!IsShutdown(ex, ct))
            {
                logger.LogError(ex, "Outbox prune failed; delivery was unaffected.");
            }
        }

        return delivered;
    }

    /// <summary>
    /// True only for OUR shutdown. A cancellation raised inside a transport is that transport's
    /// own timeout, not a request to stop the worker.
    ///
    /// This distinction is the whole point: HttpClient throws TaskCanceledException — which
    /// derives from OperationCanceledException — when its own Timeout elapses, and that is the
    /// most common failure of any real email or webhook transport. Treating it as shutdown meant
    /// the attempt was never recorded, the lease was never released, the message never
    /// dead-lettered, and the rest of the batch was abandoned.
    /// </summary>
    private static bool IsShutdown(Exception ex, CancellationToken ct)
        => ex is OperationCanceledException && ct.IsCancellationRequested;

    private async Task<bool> DeliverAsync(OutboxMessage claimed, CancellationToken ct)
    {
        // A scope per message, entering that message's tenant.
        //
        // What actually enforces this is the QUERY FILTER, not TenantGuard: without an ambient
        // tenant the re-read below matches nothing and the method returns before any save.
        // TenantGuard is the backstop behind it and never fires on this path. Worth stating
        // precisely — a later "why is this null?" fix that adds IgnoreQueryFilters() would move
        // onto the path where the guard IS the only defence, while believing it already was.
        await using var scope = scopeFactory.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<IBackgroundTenantScope>().UseTenant(claimed.TenantId);

        var context = scope.ServiceProvider.GetRequiredService<TContext>();

        var message = await context.Set<OutboxMessage>().FirstOrDefaultAsync(m => m.Id == claimed.Id, ct);
        if (message is null) return false;

        var transport = scope.ServiceProvider.GetServices<IOutboxTransport>()
            .FirstOrDefault(t => t.Transport == message.Transport);

        var result = transport is null
            // Dead-lettered rather than retried forever: no amount of waiting will conjure a
            // transport, and a message retrying on a permanent misconfiguration hides it.
            ? DeliveryResult.Fatal($"No transport registered for '{message.Transport}'.")
            : await SendAsync(transport, message, ct);

        OutboxDispatcher.Apply(message, result, clock.GetUtcNow());
        await context.SaveChangesAsync(ct);

        if (!result.Succeeded)
        {
            logger.LogWarning(
                "Outbox message {MessageId} ({Transport}) failed: {Error}. Status now {Status}.",
                message.Id, message.Transport, result.Error, message.Status);
        }

        return result.Succeeded;
    }

    private async Task<DeliveryResult> SendAsync(
        IOutboxTransport transport, OutboxMessage message, CancellationToken ct)
    {
        try
        {
            return await transport.SendAsync(message, ct);
        }
        catch (Exception ex) when (!IsShutdown(ex, ct))
        {
            // A transport that throws instead of returning a result must not lose the reason or
            // strand the lease. Treated as transient: a transport timeout or a bug in one should
            // not permanently discard a customer's invitation on its first attempt.
            return DeliveryResult.Transient($"{ex.GetType().Name}: {ex.Message}");
        }
    }
}

public static class OutboxWorkerExtensions
{
    public static IServiceCollection AddOutboxWorker<TContext>(
        this IServiceCollection services, OutboxWorkerOptions options)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddHostedService(sp => new OutboxWorker<TContext>(
            sp.GetRequiredService<IServiceScopeFactory>(),
            options,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<OutboxWorker<TContext>>>()));

        return services;
    }
}
