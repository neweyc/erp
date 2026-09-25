using AppPlatform.Auth;
using AppPlatform.Ids;
using AppPlatform.Outbox;
using AppPlatform.Tickets.Data;

namespace AppPlatform.Tickets.Tests;

internal static class Fake
{
    public static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    public static Caller Caller(params string[] licensedApps) => new()
    {
        PrincipalId = Guid.NewGuid(),
        Kind = PrincipalKind.User,
        UserId = Guid.NewGuid(),
        TenantId = 1,
        CompanyId = 1,
        Role = "member",
        LicensedApps = licensedApps.Length == 0 ? ["tickets"] : licensedApps,
    };

    /// <summary>Generated, never a hand-written literal — a wrong-length body fails TryParse
    /// and every test then reports NotFound while pointing at the wrong cause.</summary>
    public static PublishedEmployee Employee(string status = "Active", string? publicId = null) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = 1,
        CompanyId = 1,
        PublicId = publicId ?? PublicId.New("emp").ToString(),
        FirstName = "Ada",
        LastName = "Lovelace",
        DisplayName = "Ada Lovelace",
        Status = status,
    };

    public static Ticket Ticket(TicketStatus status = TicketStatus.Open) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = 1,
        PublicId = PublicId.New("tkt").ToString(),
        Title = "Printer jammed",
        Status = status,
        CreatedAt = Now,
    };
}

internal sealed class RecordingOutbox : IOutbox
{
    public List<OutboxEvent> Events { get; } = [];
    public List<OutboxMessage> Messages { get; } = [];

    public OutboxEvent AddEvent(
        string aggregateType, string aggregatePublicId, long aggregateVersion,
        string eventType, string payload)
    {
        var e = new OutboxEvent
        {
            AggregateType = aggregateType, AggregatePublicId = aggregatePublicId,
            AggregateVersion = aggregateVersion, EventType = eventType, Payload = payload,
        };
        Events.Add(e);
        return e;
    }

    public OutboxMessage AddMessage(string transport, string destination, string payload)
    {
        var m = new OutboxMessage { Transport = transport, Destination = destination, Payload = payload };
        Messages.Add(m);
        return m;
    }
}
