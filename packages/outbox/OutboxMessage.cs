using AppPlatform.Tenancy;

namespace AppPlatform.Outbox;

public enum OutboxStatus
{
    Pending,
    Succeeded,
    /// <summary>Attempts exhausted. Retained and visible — never silently dropped.</summary>
    Dead,
}

public static class OutboxTransports
{
    public const string Email = "email";
    public const string Webhook = "webhook";
}

/// <summary>
/// One delivery attempt-set: what the worker actually sends.
///
/// Separate from <see cref="OutboxEvent"/> on purpose. A single <c>delivered</c> flag on the
/// event cannot represent two webhook endpoints where one succeeds and one is failing — and
/// the moment a second subscriber exists, that flag starts lying about the first. So an
/// event fans out to one message per subscription, each with its own attempts and status.
///
/// An email has no event: it is a message on its own, which is how one mechanism serves both
/// without a second delivery system.
/// </summary>
public class OutboxMessage : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public int TenantId { get; set; }

    /// <summary>Null for an email; set when this message is the fan-out of a domain event.</summary>
    public Guid? EventId { get; set; }

    public required string Transport { get; set; }

    /// <summary>An email address, or a webhook subscription's public id.</summary>
    public required string Destination { get; set; }

    public required string Payload { get; set; }

    public OutboxStatus Status { get; set; } = OutboxStatus.Pending;

    public int Attempts { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>
    /// Held by a worker that has claimed this row. Without a lease, two workers each read
    /// the same pending row and both send it — and "at least once" becomes "twice, every
    /// time".
    /// </summary>
    public DateTimeOffset? LockedUntil { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Truncated. A provider that returns an HTML error page must not fill the table.</summary>
    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
