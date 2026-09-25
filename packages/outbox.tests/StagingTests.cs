using AppPlatform.Ids;
using AppPlatform.Outbox;
using AppPlatform.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace AppPlatform.Outbox.Tests;

public class OutboxDbContext(DbContextOptions options, ITenantProvider tenant)
    : TenantedDbContext(options, tenant)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddOutbox("tickets");
        base.OnModelCreating(modelBuilder);
    }
}

public class StagingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private static (OutboxDbContext Db, Outbox Outbox) Open(string name)
    {
        var tenant = new AmbientTenantProvider();
        tenant.UseTenant(1);

        var db = new OutboxDbContext(
            new DbContextOptionsBuilder().UseInMemoryDatabase(name).Options, tenant);

        return (db, new Outbox(db, new FakeTimeProvider(Now)));
    }

    [Fact]
    public async Task Enqueueing_does_not_save_on_its_own()
    {
        var (db, outbox) = Open(nameof(Enqueueing_does_not_save_on_its_own));

        outbox.AddMessage(OutboxTransports.Email, "a@b.c", "{}");

        // THE property of this package. If enqueueing saved, a business write that later
        // rolled back would leave the notification behind — which is EMS's send-then-commit
        // bug wearing a different hat.
        Assert.Empty(await db.Set<OutboxMessage>().ToListAsync());
        Assert.Single(db.ChangeTracker.Entries<OutboxMessage>());
    }

    [Fact]
    public async Task A_message_commits_with_the_callers_transaction()
    {
        var (db, outbox) = Open(nameof(A_message_commits_with_the_callers_transaction));

        outbox.AddMessage(OutboxTransports.Email, "a@b.c", """{"kind":"invite"}""");
        await db.SaveChangesAsync();

        var saved = Assert.Single(await db.Set<OutboxMessage>().ToListAsync());
        Assert.Equal(OutboxStatus.Pending, saved.Status);
        Assert.Equal(Now, saved.NextAttemptAt);
        Assert.Equal(1, saved.TenantId);
        Assert.Equal(0, saved.Attempts);
    }

    [Fact]
    public async Task An_event_is_stamped_with_the_tenant_like_any_other_row()
    {
        var (db, outbox) = Open(nameof(An_event_is_stamped_with_the_tenant_like_any_other_row));

        outbox.AddEvent("employee", PublicId.New("emp").ToString(), 1, "employee.hired", """{"x":1}""");
        await db.SaveChangesAsync();

        var saved = Assert.Single(await db.Set<OutboxEvent>().ToListAsync());
        Assert.Equal(1, saved.TenantId);
        Assert.Equal(Now, saved.OccurredAt);
    }

    [Fact]
    public void An_internal_key_is_refused_as_an_aggregate_id()
    {
        var (_, outbox) = Open(nameof(An_internal_key_is_refused_as_an_aggregate_id));

        // A subscriber stores whatever we send. An internal key in a payload is permanent and
        // cannot be re-keyed, so this fails at the point of emission rather than at the point
        // someone notices.
        var ex = Assert.Throws<ArgumentException>(() =>
            outbox.AddEvent("employee", Guid.NewGuid().ToString(), 1, "employee.hired", "{}"));

        Assert.Contains("not a public id", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_version_of_zero_is_refused()
    {
        var (_, outbox) = Open(nameof(A_version_of_zero_is_refused));

        // A consumer cannot detect a gap in a sequence that never advances, so an event
        // without a real version silently lacks the guarantee the column exists to provide.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            outbox.AddEvent("employee", PublicId.New("emp").ToString(), 0, "employee.hired", "{}"));
    }

    [Fact]
    public void Retention_keeps_dead_letters_longest()
    {
        // The rows somebody still has to act on must outlive the ones nobody will read.
        Assert.True(OutboxRetention.DeadMessages > OutboxRetention.Events);
        Assert.True(OutboxRetention.Events > OutboxRetention.SucceededMessages);
    }
}
