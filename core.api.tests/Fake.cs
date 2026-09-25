using AppPlatform.Auth;
using AppPlatform.Core.Data;
using AppPlatform.Ids;
using AppPlatform.Outbox;

namespace AppPlatform.Core.Tests;

internal static class Fake
{
    public static Caller Caller(string role = "admin") => new()
    {
        PrincipalId = Guid.NewGuid(),
        Kind = PrincipalKind.User,
        UserId = Guid.NewGuid(),
        TenantId = 1,
        CompanyId = 1,
        Role = role,
    };

    /// <summary>
    /// The public id is GENERATED, never a hand-written literal. Writing one by hand produced a
    /// 23-character body against a 25-character format, so every invite test failed TryParse
    /// and returned NotFound before reaching the rule it was testing — six tests all pointing
    /// at the wrong cause.
    /// </summary>
    public static Employee Employee(
        string email = "ada@example.com",
        EmployeeStatus status = EmployeeStatus.Active,
        string? publicId = null)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = 1,
            CompanyId = 1,
            PublicId = publicId ?? PublicId.New("emp").ToString(),
            FirstName = "Ada",
            LastName = "Lovelace",
            Email = email,
            Status = status,
        };
}

/// <summary>Records what was staged, so a test can assert the outbox row without a database.</summary>
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
            AggregateType = aggregateType,
            AggregatePublicId = aggregatePublicId,
            AggregateVersion = aggregateVersion,
            EventType = eventType,
            Payload = payload,
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
