using AppPlatform.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace AppPlatform.PrivilegeTests;

[Collection(nameof(PrivilegeCollection))]
public class TenantWriteIsolationTests(PrivilegeFixture fixture)
{
    private sealed class Ticket : ITenantScoped
    {
        public Guid Id { get; set; }
        public int TenantId { get; set; }
        public string PublicId { get; set; } = "";
        public string Title { get; set; } = "";
    }

    private sealed class TicketContext(DbContextOptions options, ITenantProvider tenant)
        : TenantedDbContext(options, tenant)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<Ticket>(e =>
            {
                e.ToTable("ticket", "tickets");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasColumnName("id");
                e.Property(x => x.TenantId).HasColumnName("tenant_id");
                e.Property(x => x.PublicId).HasColumnName("public_id");
                e.Property(x => x.Title).HasColumnName("title");
            });
            base.OnModelCreating(model);
        }
    }

    private TicketContext Open(int tenantId)
    {
        var tenant = new AmbientTenantProvider();
        tenant.UseTenant(tenantId);
        return new TicketContext(new DbContextOptionsBuilder()
            .UseNpgsql(fixture.ConnectionStringAs("ap_tickets_rt")).Options, tenant);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Forged_attached_write_cannot_change_another_tenants_row(bool delete, bool sync)
    {
        var id = Guid.NewGuid();
        await using (var owner = Open(1))
        {
            owner.Add(new Ticket { Id = id, PublicId = id.ToString(), Title = "original" });
            await owner.SaveChangesAsync();
        }
        await using (var attacker = Open(2))
        {
            var forged = new Ticket { Id = id, TenantId = 2, Title = "stolen" };
            if (delete) attacker.Remove(forged); else attacker.Update(forged);
            if (sync) Assert.Throws<DbUpdateConcurrencyException>(() => attacker.SaveChanges());
            else await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => attacker.SaveChangesAsync());
        }
        await using var check = Open(1);
        var saved = await check.Set<Ticket>().SingleAsync(x => x.Id == id);
        Assert.Equal("original", saved.Title);
        Assert.Equal(1, saved.TenantId);
        check.Remove(saved);
        await check.SaveChangesAsync();
    }

    [Fact]
    public async Task Legitimate_updates_work_and_tenant_reassignment_is_rejected()
    {
        await using var db = Open(1);
        var ticket = new Ticket { Id = Guid.NewGuid(), PublicId = Guid.NewGuid().ToString(), Title = "before" };
        db.Add(ticket);
        await db.SaveChangesAsync();
        ticket.Title = "after";
        await db.SaveChangesAsync();
        ticket.TenantId = 2;
        await Assert.ThrowsAsync<TenantScopeViolationException>(() => db.SaveChangesAsync());
        ticket.TenantId = 1;
        db.Remove(ticket);
        await db.SaveChangesAsync();
    }
}
