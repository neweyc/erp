namespace AppPlatform.Outbox;

/// <summary>
/// The retry schedule. Pure and deterministic — no jitter — because a schedule you cannot
/// predict is a schedule you cannot test, and the thundering-herd problem jitter solves does
/// not exist at one worker per service.
/// </summary>
public static class OutboxBackoff
{
    /// <summary>
    /// After this many failures a message is dead-lettered rather than retried forever.
    /// Six attempts spans roughly half an hour, which covers a provider blip and a restart
    /// without hammering a genuinely broken endpoint for days.
    /// </summary>
    public const int MaxAttempts = 6;

    private static readonly TimeSpan[] Schedule =
    [
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
    ];

    /// <summary>
    /// When to try again, or null when the message should be dead-lettered.
    /// </summary>
    /// <param name="attempts">
    /// Failures SO FAR, counting the one just recorded — so the first failure passes 1 and
    /// gets the first delay. Taking this as the count before the increment is an off-by-one
    /// that silently skips the shortest step, which is the one that matters: it is the retry
    /// that recovers a provider blip before anyone notices.
    /// </param>
    public static TimeSpan? DelayAfter(int attempts)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempts);

        if (attempts >= MaxAttempts) return null;

        // Held at the longest step rather than growing without bound; MaxAttempts is what
        // ends the sequence, not the length of the table.
        return Schedule[Math.Min(Math.Max(attempts - 1, 0), Schedule.Length - 1)];
    }

    /// <summary>
    /// How long a claim is held. Must comfortably exceed the slowest send, or a second worker
    /// picks up a message still in flight and delivers it twice.
    /// </summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
}
