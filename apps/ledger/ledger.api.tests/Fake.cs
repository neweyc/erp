using AppPlatform.Auth;
using AppPlatform.Ids;
using AppPlatform.Ledger.Data;
using AppPlatform.Outbox;

namespace AppPlatform.Ledger.Tests;

internal static class Fake
{
    public static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    public static readonly DateOnly Today = new(2026, 9, 25);

    public static Caller Caller() => new()
    {
        PrincipalId = Guid.NewGuid(),
        Kind = PrincipalKind.User,
        UserId = Guid.NewGuid(),
        TenantId = 1,
        CompanyId = 1,
        Role = "admin",
        LicensedApps = ["ledger"],
    };

    public static PublishedCompany Company(int id = 1) => new()
    {
        Id = id, TenantId = 1, PublicId = PublicId.New("co").ToString(), Name = $"Company {id}", Active = true,
    };

    /// <summary>Generated public ids, never hand-written: a wrong-length literal fails TryParse and
    /// every test then reports "not found" for the wrong reason.</summary>
    public static Account Account(string code, AccountType type = AccountType.Asset, int companyId = 1) => new()
    {
        Id = Guid.NewGuid(), TenantId = 1, CompanyId = companyId,
        PublicId = PublicId.New("acct").ToString(), Code = code, Name = $"Account {code}", Type = type,
    };
}

/// <summary>Records what was staged, so a test can assert the event without a database.</summary>
internal sealed class RecordingOutbox : IOutbox
{
    public List<OutboxEvent> Events { get; } = [];

    public OutboxEvent AddEvent(
        string aggregateType, string aggregatePublicId, long aggregateVersion, string eventType, string payload)
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
        => throw new NotSupportedException("The ledger sends no messages.");
}
