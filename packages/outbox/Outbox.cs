using AppPlatform.Ids;
using Microsoft.EntityFrameworkCore;

namespace AppPlatform.Outbox;

public sealed class Outbox(DbContext context, TimeProvider clock) : IOutbox
{
    public OutboxEvent AddEvent(
        string aggregateType,
        string aggregatePublicId,
        long aggregateVersion,
        string eventType,
        string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aggregateType);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        // A version of zero means the caller has no counter, and a consumer cannot detect a
        // gap in a sequence that never advances. Refuse rather than emit an event whose
        // ordering guarantee is silently absent.
        ArgumentOutOfRangeException.ThrowIfLessThan(aggregateVersion, 1);

        // An internal key in a payload a subscriber stores is permanent, so the public id is
        // required to BE one rather than merely be called one.
        if (!PublicId.TryParseAny(aggregatePublicId, out _))
        {
            throw new ArgumentException(
                $"'{aggregatePublicId}' is not a public id. An event payload must never carry " +
                "an internal key: a subscriber will store it, and it cannot then be re-keyed.",
                nameof(aggregatePublicId));
        }

        var @event = new OutboxEvent
        {
            AggregateType = aggregateType,
            AggregatePublicId = aggregatePublicId,
            AggregateVersion = aggregateVersion,
            EventType = eventType,
            Payload = payload,
            OccurredAt = clock.GetUtcNow(),
        };

        // Add, never save. The caller's SaveChanges is what makes this atomic with the change
        // the event describes.
        context.Add(@event);
        return @event;
    }

    public OutboxMessage AddMessage(string transport, string destination, string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transport);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        var now = clock.GetUtcNow();

        var message = new OutboxMessage
        {
            Transport = transport,
            Destination = destination,
            Payload = payload,
            // Due immediately. The worker polls, so "now" means "on the next sweep".
            NextAttemptAt = now,
            CreatedAt = now,
        };

        context.Add(message);
        return message;
    }
}
