namespace AppPlatform.Auth;

/// <summary>
/// When to write <c>last_seen_at</c>. An unthrottled write per request turns every read
/// into a write transaction; too lazy a write makes an idle window fire EARLY, which
/// signs people out mid-form.
/// </summary>
public static class ActivityPolicy
{
    /// <summary>Informational only — no idle window is configured, so precision costs more than it buys.</summary>
    public static readonly TimeSpan RelaxedInterval = TimeSpan.FromMinutes(5);

    /// <summary>A tenant has configured an idle window, so staleness now has a user-visible consequence.</summary>
    public static readonly TimeSpan ActiveInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Paths that must NOT count as activity. The notifications bell polls on a timer, so
    /// an idle timeout keyed on request traffic alone would never fire while a tab sat
    /// open — a control that silently does nothing.
    ///
    /// Excluded by PATH, never by a client-supplied header, so it cannot be spoofed by a
    /// caller that would rather not be timed out.
    /// </summary>
    private static readonly string[] BackgroundPollPrefixes =
    [
        "/api/core/v1/notifications/poll",
    ];

    public static bool IsBackgroundPoll(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var normalized = path.TrimEnd('/');

        return BackgroundPollPrefixes.Any(prefix =>
            normalized.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));
    }

    public static TimeSpan IntervalFor(int? idleTimeoutMinutes)
        => idleTimeoutMinutes is null ? RelaxedInterval : ActiveInterval;

    public static bool ShouldTouch(
        string path, DateTimeOffset lastSeenAt, DateTimeOffset now, int? idleTimeoutMinutes)
    {
        if (IsBackgroundPoll(path)) return false;

        return now - lastSeenAt >= IntervalFor(idleTimeoutMinutes);
    }

    /// <summary>
    /// The explicit activity ping from the browser's idle watcher. It touches
    /// UNCONDITIONALLY, bypassing the throttle above.
    ///
    /// The two clocks measure different things — the browser watches keystrokes, the server
    /// watches requests — and a long form produces plenty of the first and none of the
    /// second. Someone typing past the window would otherwise have their SAVE rejected,
    /// losing exactly the work the warning exists to protect. The throttle is what lets the
    /// clocks drift, so this path must never be subject to it, and must never be treated
    /// as a background poll.
    /// </summary>
    public const string ActivityPingPath = "/api/core/v1/auth/activity";
}
