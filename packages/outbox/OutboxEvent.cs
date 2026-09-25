using AppPlatform.Tenancy;

namespace AppPlatform.Outbox;

/// <summary>
/// A domain event: immutable, written once, in the same transaction as the change it
/// describes.
///
/// Emitted from the first feature even with nothing subscribed. The reason is retrofit
/// cost, NOT history: adding emission later means revisiting every handler that already
/// exists, and the one that gets missed is silent — no test fails when an event is not
/// raised. Events are pruned on a retention window, so this buys the window, not the past.
/// A new subscriber is onboarded by snapshot-then-subscribe.
/// </summary>
public class OutboxEvent : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public int TenantId { get; set; }

    /// <summary>e.g. <c>employee</c>. Names the kind of thing, not the table.</summary>
    public required string AggregateType { get; set; }

    /// <summary>
    /// The aggregate's PUBLIC id. An internal key must never reach a payload a subscriber
    /// could store.
    /// </summary>
    public required string AggregatePublicId { get; set; }

    /// <summary>
    /// Monotonic per aggregate, owned by the aggregate itself (its version column), not by
    /// the outbox.
    ///
    /// This exists so a consumer can detect a GAP and reorder what it cares about. There is
    /// deliberately no global ordering promise: guaranteeing one across tenants and
    /// aggregates is expensive and, offered casually, is a lie.
    /// </summary>
    public required long AggregateVersion { get; set; }

    /// <summary>e.g. <c>employee.hired</c>. Part of the published contract once anything subscribes.</summary>
    public required string EventType { get; set; }

    /// <summary>
    /// JSON. Deliberately THIN: public ids and a few stable, non-sensitive fields, never PII.
    /// A fat payload posts employee data to whatever URL a customer typed into a form, which
    /// would make the field encryption and the schema grants elsewhere in this repo
    /// pointless. Subscribers call back for detail, under their own scopes.
    /// </summary>
    public required string Payload { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}
