using AppPlatform.Outbox;
using AppPlatform.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace AppPlatform.PrivilegeTests;

/// <summary>
/// The claim, against real PostgreSQL. This cannot be unit-tested: the guarantee lives in row
/// locking inside a single statement, and InMemory has no locks, no transactions worth the
/// name, and would happily pass a "select then update" implementation that double-sends in
/// production.
/// </summary>
[Collection(nameof(PrivilegeCollection))]
public class OutboxClaimTests(PrivilegeFixture fixture)
{
    /// <summary>
    /// Read per use, not captured once. A static readonly snapshot is initialised before the
    /// first test seeds anything, so every message is created a few milliseconds AFTER it and
    /// nothing is ever due — a failure that looks like a broken claim query.
    /// </summary>
    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    private sealed class Ctx(DbContextOptions options, ITenantProvider tenant)
        : TenantedDbContext(options, tenant)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.AddOutbox("tickets");
            base.OnModelCreating(modelBuilder);
        }
    }

    private Ctx Open(int tenantId = 1)
    {
        var tenant = new AmbientTenantProvider();
        tenant.UseTenant(tenantId);

        return new Ctx(new DbContextOptionsBuilder()
            .UseNpgsql(fixture.ConnectionStringAs("ap_tickets_rt")).Options, tenant);
    }

    private async Task<List<Guid>> SeedAsync(int count, string tag)
    {
        await using var db = Open();
        var outbox = new AppPlatform.Outbox.Outbox(db, TimeProvider.System);

        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            ids.Add(outbox.AddMessage(OutboxTransports.Email, $"{tag}-{i}@b.c", "{}").Id);
        }

        await db.SaveChangesAsync();
        return ids;
    }

    private async Task CleanupAsync(IEnumerable<Guid> ids)
    {
        await using var db = Open();
        await db.Set<OutboxMessage>().Where(m => ids.Contains(m.Id)).ExecuteDeleteAsync();
    }

    [Fact]
    public void The_claim_sql_interpolates_the_schema_and_skips_locked_rows()
    {
        using var db = Open();

        var sql = new OutboxClaimStore(db, "tickets").ClaimSql;

        // Both mistakes are silent. An uninterpolated schema fails only at runtime; a missing
        // SKIP LOCKED does not break correctness but serialises the workers, so throughput
        // quietly collapses under exactly the load that made a second worker necessary.
        Assert.Contains("tickets.outbox_message", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("_schema", sql, StringComparison.Ordinal);
        Assert.Contains("FOR UPDATE SKIP LOCKED", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_due_message_is_claimed_and_leased()
    {
        var ids = await SeedAsync(1, nameof(A_due_message_is_claimed_and_leased));
        try
        {
            await using var db = Open();
            var claimed = await new OutboxClaimStore(db, "tickets").ClaimAsync(10, Now);

            var message = Assert.Single(claimed, m => ids.Contains(m.Id));
            Assert.NotNull(message.LockedUntil);
            Assert.True(message.LockedUntil > Now);
        }
        finally { await CleanupAsync(ids); }
    }

    [Fact]
    public async Task Two_concurrent_workers_never_claim_the_same_message()
    {
        var ids = await SeedAsync(20, nameof(Two_concurrent_workers_never_claim_the_same_message));
        try
        {
            await using var first = Open();
            await using var second = Open();

            // Run genuinely concurrently. The safety property is that the claim is ONE
            // statement, so two executions cannot lease the same row — the second blocks on
            // the lock, re-evaluates, and sees locked_until already set. Split into a select
            // followed by an update, this test is what fails, and the symptom in production is
            // every message delivered twice.
            var results = await Task.WhenAll(
                new OutboxClaimStore(first, "tickets").ClaimAsync(20, Now),
                new OutboxClaimStore(second, "tickets").ClaimAsync(20, Now));

            // Restricted to this test's rows: the fixture is shared, so counting everything
            // claimed would make the assertion depend on what else is in the table.
            var a = results[0].Select(m => m.Id).Where(ids.Contains).ToHashSet();
            var b = results[1].Select(m => m.Id).Where(ids.Contains).ToHashSet();

            Assert.Empty(a.Intersect(b));
            Assert.Equal(ids.Count, a.Count + b.Count);
        }
        finally { await CleanupAsync(ids); }
    }

    [Fact]
    public async Task A_leased_message_is_not_reclaimed_while_the_lease_holds()
    {
        var ids = await SeedAsync(3, nameof(A_leased_message_is_not_reclaimed_while_the_lease_holds));
        try
        {
            await using var db = Open();
            var store = new OutboxClaimStore(db, "tickets");

            await store.ClaimAsync(10, Now);
            var second = await store.ClaimAsync(10, Now);

            Assert.DoesNotContain(second, m => ids.Contains(m.Id));
        }
        finally { await CleanupAsync(ids); }
    }

    [Fact]
    public async Task An_expired_lease_is_reclaimed_so_a_crashed_worker_does_not_strand_work()
    {
        var ids = await SeedAsync(2, nameof(An_expired_lease_is_reclaimed_so_a_crashed_worker_does_not_strand_work));
        try
        {
            await using var db = Open();
            var store = new OutboxClaimStore(db, "tickets");

            await store.ClaimAsync(10, Now);

            // A worker that dies mid-send never releases its lease. If expiry did not return
            // the row to the queue, that message would sit Pending and undelivered forever.
            var later = await store.ClaimAsync(10, Now + OutboxBackoff.LeaseDuration.Add(TimeSpan.FromMinutes(1)));

            Assert.Equal(ids.Count, later.Count(m => ids.Contains(m.Id)));
        }
        finally { await CleanupAsync(ids); }
    }

    [Fact]
    public async Task A_message_not_yet_due_is_left_alone()
    {
        var ids = await SeedAsync(1, nameof(A_message_not_yet_due_is_left_alone));
        try
        {
            await using (var db = Open())
            {
                await db.Set<OutboxMessage>().Where(m => ids.Contains(m.Id))
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.NextAttemptAt, Now.AddHours(1)));
            }

            await using var check = Open();
            var claimed = await new OutboxClaimStore(check, "tickets").ClaimAsync(10, Now);

            Assert.DoesNotContain(claimed, m => ids.Contains(m.Id));
        }
        finally { await CleanupAsync(ids); }
    }
}
