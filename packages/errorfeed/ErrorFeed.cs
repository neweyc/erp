using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using AppPlatform.Ids;
using Microsoft.Extensions.Logging;

namespace AppPlatform.ErrorFeed;

/// <summary>
/// One entry in the operator error feed. These fields and NO others: the feed is metadata only,
/// by design (platform.api/CLAUDE.md). There is no message and no stack trace, because both
/// routinely carry customer data — a name in an exception message, an email in a failed query —
/// and the platform must not hold it. The detail stays in the reporting service's own log, found
/// there by <see cref="Reference"/>.
/// </summary>
/// <param name="Reference">Quoted to support by the customer who saw the error; "err_…".</param>
/// <param name="Fingerprint">Groups occurrences of the same fault; 16 hex characters.</param>
/// <param name="TenantId">The tenant the request was for, when there was one.</param>
/// <param name="Count">This occurrence plus any identical ones dropped by the rate limit before it.</param>
public sealed record ErrorOccurrence(
    string Reference, string Fingerprint, string App, int? TenantId, int Count, DateTimeOffset OccurredAt);

public static class ErrorReference
{
    public const string Prefix = "err";

    public static string New() => PublicId.New(Prefix).ToString();
}

/// <summary>
/// What makes two occurrences "the same fault", computed WITHOUT the message, which can hold
/// customer data. For an exception: its type, its inner exceptions' types, and the methods at the
/// top of its stack. For a log entry with no exception: its category, event id, and message
/// TEMPLATE — "Outbox sweep failed; retrying", never the values that fill it.
/// </summary>
public static class ErrorFingerprint
{
    /// <summary>Enough frames to tell faults apart; few enough that a refactor lower down does not.</summary>
    private const int FramesConsidered = 5;

    public static string ForException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var shape = new StringBuilder();
        for (var e = exception; e is not null; e = e.InnerException)
            shape.Append(e.GetType().FullName).Append('|');

        var frames = new StackTrace(exception.GetBaseException(), fNeedFileInfo: false).GetFrames();
        foreach (var frame in frames.Take(FramesConsidered))
        {
            var method = frame.GetMethod();
            shape.Append(method?.DeclaringType?.FullName).Append('.').Append(method?.Name).Append('|');
        }

        return Hash(shape.ToString());
    }

    public static string ForLogEntry(string category, EventId eventId, string? messageTemplate)
        => Hash($"{category}|{eventId.Id}|{messageTemplate}");

    private static string Hash(string shape)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(shape)).AsSpan(0, 8));
}

/// <summary>
/// The in-process side of the feed: takes occurrences from anywhere and hands them to
/// <see cref="ErrorFeedWriter"/>, which sends them to the platform.
///
/// Built so that reporting an error can never make things worse:
/// - It never blocks and never throws. The queue is bounded; when it is full, occurrences are
///   dropped, not waited for.
/// - It is rate-limited per fault and tenant: the first <see cref="PerFingerprintPerMinute"/> of a
///   fingerprint for one tenant in a minute are sent individually, the rest counted and folded into
///   the next one sent for that same tenant, so a count is never credited to the wrong tenant. A
///   fault in a hot loop therefore costs a handful of rows, not a flood that buries every other one.
///   A count saturates at <see cref="MaxCount"/>, the most the platform accepts: "at least this many".
/// - Memory is bounded at <see cref="MaxTrackedFingerprints"/> budgets. When full, budgets whose
///   minute is over and which hold no uncounted occurrences are dropped first; if it is still full,
///   the NEW fault is dropped. Active budgets are never reset, or churning through fingerprints
///   would switch the rate limit off.
/// </summary>
public sealed class ErrorFeed
{
    public const int QueueCapacity = 1000;
    public const int PerFingerprintPerMinute = 10;
    public const int MaxTrackedFingerprints = 1000;

    /// <summary>The largest count platform_v1.record_error accepts.</summary>
    public const int MaxCount = 100_000;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly Channel<ErrorOccurrence> _queue = Channel.CreateBounded<ErrorOccurrence>(
        new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Budget> _budgets = [];
    private readonly TimeProvider _clock;

    public ErrorFeed(string app, TimeProvider clock)
    {
        if (!AppName.IsValid(app))
            throw new ArgumentException($"'{app}' is not a valid app name: 2-20 lowercase letters, digits or underscores.", nameof(app));

        App = app;
        _clock = clock;
    }

    /// <summary>Which service this is: "core", "tickets", "platform".</summary>
    public string App { get; }

    internal ChannelReader<ErrorOccurrence> Reader => _queue.Reader;

    /// <summary>Records one occurrence, or counts it for later if its fault is over budget. Never throws.</summary>
    public void Record(string reference, string fingerprint, int? tenantId)
    {
        try
        {
            var now = _clock.GetUtcNow();
            var key = $"{fingerprint}|{tenantId}";
            int count;

            lock (_gate)
            {
                if (!_budgets.ContainsKey(key) && !MakeRoom(now)) return;

                if (!_budgets.TryGetValue(key, out var budget) || now - budget.WindowStart >= Window)
                    budget = new Budget(now, Used: 0, Suppressed: budget?.Suppressed ?? 0);

                if (budget.Used >= PerFingerprintPerMinute)
                {
                    _budgets[key] = budget with { Suppressed = Math.Min(budget.Suppressed + 1, MaxCount - 1) };
                    return;
                }

                count = 1 + budget.Suppressed;
                _budgets[key] = budget with { Used = budget.Used + 1, Suppressed = 0 };
            }

            _queue.Writer.TryWrite(new ErrorOccurrence(reference, fingerprint, App, tenantId, count, now));
        }
        catch
        {
            // Nothing here may fail the caller, which is already handling an error of its own.
        }
    }

    /// <summary>True if a new budget fits, after dropping any that are finished with. Under the lock.</summary>
    private bool MakeRoom(DateTimeOffset now)
    {
        if (_budgets.Count < MaxTrackedFingerprints) return true;

        foreach (var (key, budget) in _budgets.ToList())
        {
            if (now - budget.WindowStart >= Window && budget.Suppressed == 0) _budgets.Remove(key);
        }

        return _budgets.Count < MaxTrackedFingerprints;
    }

    private sealed record Budget(DateTimeOffset WindowStart, int Used, int Suppressed);
}

internal static class AppName
{
    /// <summary>The same shape platform_v1.record_error accepts.</summary>
    public static bool IsValid(string? app)
        => app is { Length: >= 2 and <= 20 } && char.IsAsciiLetterLower(app[0])
           && app.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_');
}
