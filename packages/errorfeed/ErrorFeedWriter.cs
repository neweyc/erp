using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AppPlatform.ErrorFeed;

/// <summary>
/// Sends queued occurrences to the platform, one call to <c>platform_v1.record_error</c> each.
///
/// Through a published function, not a table grant: the function checks every value's shape
/// before storing it, and a service holding only EXECUTE on it can add to the feed but never read
/// it — the feed says which tenants are failing, which one tenant's services have no business
/// knowing. The same pattern as identity_v1.session_context.
///
/// A failed send is dropped and logged as a WARNING. Not retried, because a database that is
/// refusing writes is usually why errors are happening, and piling retries onto it helps nobody.
/// Not an error, because an error would come straight back into this feed.
/// </summary>
public sealed class ErrorFeedWriter : BackgroundService
{
    private readonly ErrorFeed _feed;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<ErrorFeedWriter> _logger;

    public ErrorFeedWriter(ErrorFeed feed, string connectionString, ILogger<ErrorFeedWriter> logger)
    {
        _feed = feed;
        _logger = logger;

        // Its own small pool, owned and disposed here, so a feed under load cannot take the
        // connections requests need.
        _dataSource = NpgsqlDataSource.Create(
            new NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = 2 }.ConnectionString);
    }

    public override void Dispose()
    {
        _dataSource.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var occurrence in _feed.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await using var command = _dataSource.CreateCommand(
                    "SELECT platform_v1.record_error($1, $2, $3, $4, $5, $6)");
                command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = occurrence.Reference });
                command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = occurrence.Fingerprint });
                command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = occurrence.App });
                command.Parameters.Add(new NpgsqlParameter { Value = (object?)occurrence.TenantId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer });
                command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = occurrence.Count });
                command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = occurrence.OccurredAt });

                await command.ExecuteNonQueryAsync(stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(exception, "Could not send error {ErrorReference} to the operator feed; dropped.",
                    occurrence.Reference);
            }
        }
    }
}

public static class ErrorFeedExtensions
{
    /// <summary>
    /// Mirrors this service's Error-level occurrences to the operator feed. <paramref name="app"/>
    /// names the service in the feed. <paramref name="connectionString"/> is the service's own
    /// connection; its role needs EXECUTE on platform_v1.record_error (02-grants.sql) and nothing else.
    /// </summary>
    public static IServiceCollection AddErrorFeed(this IServiceCollection services, string app, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(sp => new ErrorFeed(app, sp.GetService<TimeProvider>() ?? TimeProvider.System));

        services.AddHostedService(sp => new ErrorFeedWriter(
            sp.GetRequiredService<ErrorFeed>(), connectionString, sp.GetRequiredService<ILogger<ErrorFeedWriter>>()));

        // Logging collects every ILoggerProvider registered in the container. The correlation logger
        // is resolved on first use, not here: the logger factory is built FROM these providers.
        services.AddSingleton<ILoggerProvider>(sp => new ErrorFeedLoggerProvider(
            sp.GetRequiredService<ErrorFeed>(),
            () => sp.GetRequiredService<ILoggerFactory>().CreateLogger(ErrorFeedLoggerProvider.CorrelationCategory)));

        return services;
    }

    /// <summary>Call FIRST, before any other middleware, so it catches failures in all of them.</summary>
    public static IApplicationBuilder UseErrorFeed(this IApplicationBuilder app)
        => app.UseMiddleware<ErrorFeedMiddleware>();
}
