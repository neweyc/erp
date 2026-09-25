namespace AppPlatform.Outbox;

/// <summary>
/// How long delivered work and emitted events are kept.
///
/// This is the number the replay promise is made against, so it is a product decision with a
/// storage bill, not a default to be discovered later. **30 days**: it covers a subscriber's
/// outage and a weekend, and it is deliberately not an archive. A customer needing more is a
/// conversation, not a config change nobody priced.
/// </summary>
public static class OutboxRetention
{
    public static readonly TimeSpan Events = TimeSpan.FromDays(30);

    /// <summary>Succeeded messages go sooner: the event they came from is the record worth keeping.</summary>
    public static readonly TimeSpan SucceededMessages = TimeSpan.FromDays(7);

    /// <summary>
    /// Dead-lettered messages outlive both. They are the ones somebody still has to look at,
    /// and pruning them on the ordinary schedule would delete the evidence of the failure
    /// along with the failure.
    /// </summary>
    public static readonly TimeSpan DeadMessages = TimeSpan.FromDays(90);

    public static DateTimeOffset EventCutoff(DateTimeOffset now) => now - Events;
    public static DateTimeOffset SucceededCutoff(DateTimeOffset now) => now - SucceededMessages;
    public static DateTimeOffset DeadCutoff(DateTimeOffset now) => now - DeadMessages;
}
