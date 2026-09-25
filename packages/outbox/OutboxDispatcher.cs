namespace AppPlatform.Outbox;

/// <summary>
/// Applies one delivery result to a message: status, attempts, and when to try again. Pure, so
/// the retry and dead-letter rules are provable without a database or a transport.
/// </summary>
public static class OutboxDispatcher
{
    public static void Apply(OutboxMessage message, DeliveryResult result, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(result);

        message.Attempts++;
        message.LockedUntil = null;

        if (result.Succeeded)
        {
            message.Status = OutboxStatus.Succeeded;
            message.CompletedAt = now;
            message.LastError = null;
            return;
        }

        message.LastError = Truncate(result.Error);

        // A permanent failure skips the schedule. Spending six attempts over half an hour to
        // rediscover that an address is malformed delays every other message behind it.
        var delay = result.Permanent ? null : OutboxBackoff.DelayAfter(message.Attempts);

        if (delay is not { } wait)
        {
            // Dead, not deleted. Somebody still has to look at it, and a silently dropped
            // notification is indistinguishable from one that was never queued.
            message.Status = OutboxStatus.Dead;
            message.CompletedAt = now;
            return;
        }

        message.NextAttemptAt = now + wait;
    }

    /// <summary>
    /// A provider that answers a failed send with an HTML error page would otherwise put
    /// kilobytes into every row.
    /// </summary>
    private static string? Truncate(string? error)
        => error is null or { Length: <= 1000 } ? error : error[..1000];
}
