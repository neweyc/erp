namespace AppPlatform.Outbox;

/// <summary>
/// Stages outbox rows into the CALLER's unit of work.
///
/// Nothing here saves. That is the whole point: the record and its pending notification must
/// commit in one transaction, so if the business write rolls back the notification goes with
/// it. An implementation that called SaveChanges would reintroduce exactly the failure this
/// package replaced — EMS sent the email first and then committed, so a send that succeeded
/// against a commit that failed left the recipient holding a link to an invitation that never
/// existed.
/// </summary>
public interface IOutbox
{
    /// <summary>Records a domain event. Fan-out to subscribers happens later, in the worker.</summary>
    OutboxEvent AddEvent(
        string aggregateType,
        string aggregatePublicId,
        long aggregateVersion,
        string eventType,
        string payload);

    /// <summary>Queues a direct delivery — an email — with no event behind it.</summary>
    OutboxMessage AddMessage(string transport, string destination, string payload);
}
