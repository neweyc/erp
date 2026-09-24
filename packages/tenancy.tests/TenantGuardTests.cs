using Microsoft.EntityFrameworkCore;

namespace AppPlatform.Tenancy.Tests;

public class TenantGuardTests
{
    [Fact]
    public async Task Insert_is_stamped_with_the_current_tenant()
    {
        var (db, _) = Harness.Context(nameof(Insert_is_stamped_with_the_current_tenant), tenantId: 7);

        var widget = new Widget { Name = "unstamped" };
        db.Widgets.Add(widget);
        await db.SaveChangesAsync();

        Assert.Equal(7, widget.TenantId);
    }

    [Fact]
    public async Task Insert_without_a_tenant_context_is_refused()
    {
        var (db, _) = Harness.Context(nameof(Insert_without_a_tenant_context_is_refused));

        db.Widgets.Add(new Widget { Name = "orphan" });

        var ex = await Assert.ThrowsAsync<TenantScopeViolationException>(() => db.SaveChangesAsync());
        Assert.Contains("without a tenant context", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Insert_carrying_a_foreign_tenant_id_is_refused()
    {
        var (db, _) = Harness.Context(nameof(Insert_carrying_a_foreign_tenant_id_is_refused), tenantId: 1);

        // EMS honoured a pre-set TenantId whenever there was no ambient context, which
        // made assigning it by hand a working bypass. Here it is refused outright.
        db.Widgets.Add(new Widget { Name = "smuggled", TenantId = 2 });

        await Assert.ThrowsAsync<TenantScopeViolationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Tenant_B_cannot_update_tenant_A_row()
    {
        const string db = nameof(Tenant_B_cannot_update_tenant_A_row);

        var (a, _) = Harness.Context(db, tenantId: 1);
        var widget = new Widget { Name = "theirs" };
        a.Widgets.Add(widget);
        await a.SaveChangesAsync();

        // Reached without the filter — a tracked entity obtained by any route at all,
        // which is precisely how a FindAsync or a change-tracker hit slips through.
        var (b, _) = Harness.Context(db, tenantId: 2);
        var stolen = new Widget { Id = widget.Id, TenantId = 1, Name = "mine now" };
        b.Attach(stolen);
        b.Entry(stolen).State = EntityState.Modified;

        var ex = await Assert.ThrowsAsync<TenantScopeViolationException>(() => b.SaveChangesAsync());
        Assert.Contains("Cross-tenant write rejected", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tenant_B_cannot_delete_tenant_A_row()
    {
        const string db = nameof(Tenant_B_cannot_delete_tenant_A_row);

        var (a, _) = Harness.Context(db, tenantId: 1);
        var widget = new Widget { Name = "theirs" };
        a.Widgets.Add(widget);
        await a.SaveChangesAsync();

        var (b, _) = Harness.Context(db, tenantId: 2);
        b.Attach(new Widget { Id = widget.Id, TenantId = 1 });
        b.Remove(b.ChangeTracker.Entries<Widget>().First().Entity);

        await Assert.ThrowsAsync<TenantScopeViolationException>(() => b.SaveChangesAsync());
    }

    [Fact]
    public async Task Update_without_a_tenant_context_is_refused()
    {
        const string db = nameof(Update_without_a_tenant_context_is_refused);

        var (a, _) = Harness.Context(db, tenantId: 1);
        var widget = new Widget { Name = "theirs" };
        a.Widgets.Add(widget);
        await a.SaveChangesAsync();

        var (anon, _) = Harness.Context(db);
        anon.Attach(new Widget { Id = widget.Id, TenantId = 1, Name = "changed" });
        anon.ChangeTracker.Entries<Widget>().First().State = EntityState.Modified;

        await Assert.ThrowsAsync<TenantScopeViolationException>(() => anon.SaveChangesAsync());
    }

    [Fact]
    public void Synchronous_save_is_guarded_too()
    {
        var (db, _) = Harness.Context(nameof(Synchronous_save_is_guarded_too));

        db.Widgets.Add(new Widget { Name = "orphan" });

        // The async path is the one everything uses; a guard on only that path leaves the
        // sync path as a silent hole.
        Assert.Throws<TenantScopeViolationException>(() => db.SaveChanges());
    }

    [Fact]
    public async Task Background_scope_grants_a_tenant_to_work_with_no_request()
    {
        var (db, provider) = Harness.Context(nameof(Background_scope_grants_a_tenant_to_work_with_no_request));

        provider.UseTenant(42);
        var widget = new Widget { Name = "from a worker" };
        db.Widgets.Add(widget);
        await db.SaveChangesAsync();

        Assert.Equal(42, widget.TenantId);
    }
}
