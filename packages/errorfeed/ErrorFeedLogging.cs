using Microsoft.Extensions.Logging;

namespace AppPlatform.ErrorFeed;

/// <summary>
/// Forwards every Error and Critical log entry, from any part of the process (a request, the
/// outbox worker, a startup check), to the feed. Nothing below Error is forwarded.
///
/// Only the metadata leaves: a fingerprint of the exception's shape or the message TEMPLATE, plus a
/// reference and a tenant if the entry carries them as the structured properties
/// <see cref="ReferenceProperty"/> and <see cref="TenantProperty"/>. The rendered message, which can
/// contain customer data, is never read.
///
/// An entry that arrives without a reference (an error in background work, say) is given one here,
/// and that reference would exist only in the feed. So a warning is logged locally under the
/// <see cref="CorrelationCategory"/> saying which error it belongs to: support can then take a
/// reference from the feed and find the detail in the service's own log.
///
/// It cannot recurse: the feed's own writer and that correlation line are the categories not
/// forwarded, and both log below Error anyway.
/// </summary>
public sealed class ErrorFeedLoggerProvider(ErrorFeed feed, Func<ILogger>? correlationLogger = null) : ILoggerProvider
{
    public const string ReferenceProperty = "ErrorReference";
    public const string TenantProperty = "TenantId";
    public const string CorrelationCategory = "AppPlatform.ErrorFeed.Correlation";

    public ILogger CreateLogger(string categoryName)
        => categoryName == typeof(ErrorFeedWriter).FullName || categoryName == CorrelationCategory
            ? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance
            : new FeedLogger(feed, categoryName, correlationLogger);

    public void Dispose()
    {
    }

    private sealed class FeedLogger(ErrorFeed feed, string category, Func<ILogger>? correlationLogger) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error && logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            try
            {
                string? reference = null, template = null;
                int? tenantId = null;

                if (state is IReadOnlyList<KeyValuePair<string, object?>> properties)
                {
                    foreach (var (name, value) in properties)
                    {
                        if (name == ReferenceProperty && value is string r) reference = r;
                        else if (name == TenantProperty && value is int t) tenantId = t;
                        else if (name == "{OriginalFormat}" && value is string f) template = f;
                    }
                }

                var fingerprint = exception is not null
                    ? ErrorFingerprint.ForException(exception)
                    : ErrorFingerprint.ForLogEntry(category, eventId, template);

                if (reference is null)
                {
                    reference = ErrorReference.New();
                    correlationLogger?.Invoke().LogWarning(
                        "Error feed reference {ErrorReference} was given to the {Category} error (event {EventId}) logged alongside this.",
                        reference, category, eventId.Id);
                }

                feed.Record(reference, fingerprint, tenantId);
            }
            catch
            {
                // Logging an error must never raise another.
            }
        }
    }
}
