using AppPlatform.Outbox;
using AppPlatform.Tenancy;
using AppPlatform.Tickets.Data;
using AppPlatform.Tickets.Features.Tickets;
using AppPlatform.Tickets.Services;
using Microsoft.EntityFrameworkCore;

namespace AppPlatform.PrivilegeTests;

/// <summary>
/// Two simultaneous changes to one ticket, against real PostgreSQL.
///
/// This cannot be unit-tested: the conflict is detected by a rowcount from an UPDATE with a
/// version predicate, and InMemory has neither. Before the fix the loser did not conflict at
/// all — it committed a duplicate event version and failed on the outbox's unique index, which
/// reached the caller as a 500 naming a constraint.
/// </summary>
[Collection(nameof(PrivilegeCollection))]
public class TicketConcurrencyTests(PrivilegeFixture fixture)
{
    private TicketsDbContext Open()
    {
        var tenant = new AmbientTenantProvider();
        tenant.UseTenant(1);

        return new TicketsDbContext(
            new DbContextOptionsBuilder<TicketsDbContext>()
                .UseNpgsql(fixture.ConnectionStringAs("ap_tickets_rt"))
                .UseSnakeCaseNamingConvention().Options,
            tenant);
    }

    private async Task<string> SeedAsync()
    {
        await using var db = Open();
        var handler = new CreateTicketFeature.CreateTicketCommandHandler(
            new EFTicketService(db), new EFEmployeeLookup(db),
            new AppPlatform.Outbox.Outbox(db, TimeProvider.System), TimeProvider.System);

        var result = await handler.Handle(Caller(), new("Concurrent", null, null));
        Assert.True(result.Succeeded);

        return (await db.Tickets.OrderByDescending(t => t.CreatedAt).FirstAsync()).PublicId;
    }

    private static Auth.Caller Caller() => new()
    {
        PrincipalId = Guid.NewGuid(),
        Kind = Auth.PrincipalKind.User,
        TenantId = 1,
        CompanyId = 1,
        Role = "member",
        LicensedApps = ["tickets"],
    };

    private async Task<Api.CommandResult> CloseAsync(string ticketId)
    {
        await using var db = Open();
        return await new CloseTicketFeature.CloseTicketCommandHandler(
            new EFTicketService(db), new AppPlatform.Outbox.Outbox(db, TimeProvider.System), TimeProvider.System)
            .Handle(Caller(), ticketId);
    }

    [Fact]
    public async Task Two_simultaneous_closes_yield_one_success_and_one_stable_conflict()
    {
        var ticketId = await SeedAsync();

        // Both contexts read version 1 before either writes. Exactly one UPDATE can match the
        // version predicate.
        var results = await Task.WhenAll(CloseAsync(ticketId), CloseAsync(ticketId));

        Assert.Equal(1, results.Count(r => r.Succeeded));

        var loser = results.Single(r => !r.Succeeded);

        // A stable code the caller can act on — not a 500 from a unique-constraint violation
        // on the outbox, and not a silent second close that rewrites when it happened.
        Assert.Equal(Api.CommandOutcome.Conflict, loser.Outcome);
        Assert.Contains(loser.ProblemCode, new[]
        {
            TicketProblems.ConcurrentChange, TicketProblems.AlreadyClosed,
        });
    }

    [Fact]
    public async Task Only_one_event_is_written_for_the_change_that_won()
    {
        var ticketId = await SeedAsync();

        await Task.WhenAll(CloseAsync(ticketId), CloseAsync(ticketId));

        await using var db = Open();
        var closed = await db.Set<OutboxEvent>()
            .Where(e => e.AggregatePublicId == ticketId && e.EventType == "ticket.closed")
            .ToListAsync();

        // The aggregate version is the consumer's gap detector; two events claiming the same
        // version would make it meaningless in exactly the way it exists to prevent.
        Assert.Single(closed);
    }

    [Fact]
    public async Task A_sequential_close_still_works()
    {
        var ticketId = await SeedAsync();

        // Guards the guard: a concurrency token that rejected everything would also pass the
        // test above, because one of the two would still be the loser.
        Assert.True((await CloseAsync(ticketId)).Succeeded);
    }
}
