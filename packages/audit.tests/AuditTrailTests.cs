using System.Text.Json;
using AppPlatform.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace AppPlatform.Audit.Tests;

public enum WidgetStatus { Draft, Live }

public class Widget : IAuditable
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public int TenantId { get; set; }
    public string PublicId { get; set; } = $"wid_{Guid.NewGuid():N}";
    public string Name { get; set; } = "";
    public string? Note { get; set; }
    public WidgetStatus Status { get; set; }

    [AuditRedacted]
    public string Secret { get; set; } = "";
}

/// <summary>Tenant-scoped but NOT auditable — stands in for sessions and outbox rows.</summary>
public class Receipt : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public int TenantId { get; set; }
}

public class TestDbContext(DbContextOptions options, ITenantProvider tenant, IAuditActor actor, TimeProvider clock)
    : AuditedDbContext(options, tenant, actor, clock)
{
    protected override string AuditSchema => "test";

    public DbSet<Widget> Widgets => Set<Widget>();
    public DbSet<Receipt> Receipts => Set<Receipt>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Widget>().ToTable("widget");
        modelBuilder.Entity<Receipt>().ToTable("receipt");
        base.OnModelCreating(modelBuilder);
    }
}

public class AuditTrailTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Ada = Guid.CreateVersion7();

    private readonly string _database = Guid.NewGuid().ToString();

    /// <summary>A fresh context over this test's database, for one tenant and one actor.</summary>
    private TestDbContext Context(int tenantId = 1, AuditActor? actor = null, bool noActor = false)
    {
        var tenant = new AmbientTenantProvider();
        tenant.UseTenant(tenantId);

        var options = new DbContextOptionsBuilder()
            .UseInMemoryDatabase(_database)
            .Options;

        return new TestDbContext(
            options, tenant,
            noActor ? new AmbientAuditActor() : new AmbientAuditActor(actor ?? AuditActor.User(Ada)),
            new FakeTimeProvider(Now));
    }

    private async Task<Widget> SeedWidget()
    {
        await using var db = Context();
        var widget = new Widget { Name = "Sprocket", Secret = "s3cret" };
        db.Widgets.Add(widget);
        await db.SaveChangesAsync();
        return widget;
    }

    private async Task<List<AuditEntry>> Rows(int tenantId = 1)
    {
        await using var db = Context(tenantId);
        return await db.AuditLog.OrderBy(a => a.Id).ToListAsync();
    }

    private static JsonElement Changes(AuditEntry row) => JsonDocument.Parse(row.Changes).RootElement;

    [Fact]
    public async Task Creating_an_entity_records_who_created_it_and_its_values()
    {
        var widget = await SeedWidget();

        var row = Assert.Single(await Rows());
        Assert.Equal(AuditAction.Created, row.Action);
        Assert.Equal(AuditActorKind.User, row.ActorKind);
        Assert.Equal(Ada, row.ActorId);
        Assert.Equal(Now, row.OccurredAt);
        Assert.Equal("widget", row.EntityType);
        Assert.Equal(widget.PublicId, row.EntityId);
        // Stamped by the tenant guard, not set by the audit code.
        Assert.Equal(1, row.TenantId);

        var name = Changes(row).GetProperty("Name");
        Assert.Equal(JsonValueKind.Null, name.GetProperty("old").ValueKind);
        Assert.Equal("Sprocket", name.GetProperty("new").GetString());
        // Enums by name, so reordering the enum cannot change what history says.
        Assert.Equal("Draft", Changes(row).GetProperty("Status").GetProperty("new").GetString());
    }

    [Fact]
    public async Task Identity_columns_and_null_values_are_not_recorded_as_changes()
    {
        await SeedWidget();

        var changes = Changes(Assert.Single(await Rows()));
        Assert.False(changes.TryGetProperty("Id", out _));
        Assert.False(changes.TryGetProperty("TenantId", out _));
        Assert.False(changes.TryGetProperty("PublicId", out _));
        Assert.False(changes.TryGetProperty("Note", out _));
    }

    [Fact]
    public async Task A_redacted_property_records_that_it_changed_but_never_its_value()
    {
        var widget = await SeedWidget();

        await using (var db = Context())
        {
            var loaded = await db.Widgets.SingleAsync(w => w.Id == widget.Id);
            loaded.Secret = "rotated";
            await db.SaveChangesAsync();
        }

        var rows = await Rows();
        Assert.All(rows, r => Assert.DoesNotContain("s3cret", r.Changes));
        Assert.All(rows, r => Assert.DoesNotContain("rotated", r.Changes));

        var secret = Changes(rows[1]).GetProperty("Secret");
        Assert.Equal(AuditTrail.Redacted, secret.GetProperty("old").GetString());
        Assert.Equal(AuditTrail.Redacted, secret.GetProperty("new").GetString());
    }

    [Fact]
    public async Task An_update_records_only_the_columns_that_changed_with_old_and_new_values()
    {
        var widget = await SeedWidget();

        await using (var db = Context())
        {
            var loaded = await db.Widgets.SingleAsync(w => w.Id == widget.Id);
            loaded.Status = WidgetStatus.Live;
            await db.SaveChangesAsync();
        }

        var row = (await Rows())[1];
        Assert.Equal(AuditAction.Updated, row.Action);

        var changes = Changes(row);
        Assert.Equal("Draft", changes.GetProperty("Status").GetProperty("old").GetString());
        Assert.Equal("Live", changes.GetProperty("Status").GetProperty("new").GetString());
        Assert.False(changes.TryGetProperty("Name", out _));
    }

    [Fact]
    public async Task Setting_a_value_to_itself_writes_no_row()
    {
        var widget = await SeedWidget();

        await using (var db = Context())
        {
            var loaded = await db.Widgets.SingleAsync(w => w.Id == widget.Id);
            loaded.Name = "Sprocket";
            await db.SaveChangesAsync();
        }

        Assert.Single(await Rows());
    }

    [Fact]
    public async Task Deleting_records_the_values_the_entity_had()
    {
        var widget = await SeedWidget();

        await using (var db = Context())
        {
            db.Widgets.Remove(await db.Widgets.SingleAsync(w => w.Id == widget.Id));
            await db.SaveChangesAsync();
        }

        var row = (await Rows())[1];
        Assert.Equal(AuditAction.Deleted, row.Action);
        Assert.Equal(widget.PublicId, row.EntityId);
        var name = Changes(row).GetProperty("Name");
        Assert.Equal("Sprocket", name.GetProperty("old").GetString());
        Assert.Equal(JsonValueKind.Null, name.GetProperty("new").ValueKind);
    }

    [Fact]
    public async Task An_auditable_change_with_no_actor_is_refused_and_nothing_is_saved()
    {
        await using (var db = Context(noActor: true))
        {
            db.Widgets.Add(new Widget { Name = "Orphan" });
            await Assert.ThrowsAsync<AuditActorMissingException>(() => db.SaveChangesAsync());
        }

        await using var check = Context();
        Assert.Empty(await check.Widgets.ToListAsync());
        Assert.Empty(await check.AuditLog.ToListAsync());
    }

    [Fact]
    public async Task A_non_auditable_change_needs_no_actor_and_writes_no_row()
    {
        // Background work — the outbox worker — saves without any principal. It must keep working.
        await using (var db = Context(noActor: true))
        {
            db.Receipts.Add(new Receipt());
            await db.SaveChangesAsync();
        }

        Assert.Empty(await Rows());
    }

    [Fact]
    public async Task A_system_actor_is_recorded_by_name_with_no_principal_id()
    {
        await using (var db = Context(actor: AuditActor.System("provisioning")))
        {
            db.Widgets.Add(new Widget { Name = "Provisioned" });
            await db.SaveChangesAsync();
        }

        var row = Assert.Single(await Rows());
        Assert.Equal(AuditActorKind.System, row.ActorKind);
        Assert.Null(row.ActorId);
        Assert.Equal("provisioning", row.ActorName);
    }

    [Fact]
    public async Task An_audit_row_cannot_be_modified()
    {
        await SeedWidget();

        await using var db = Context();
        var row = await db.AuditLog.SingleAsync();
        row.Changes = "{}";

        await Assert.ThrowsAsync<AppendOnlyViolationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task An_audit_row_cannot_be_deleted()
    {
        await SeedWidget();

        await using var db = Context();
        db.AuditLog.Remove(await db.AuditLog.SingleAsync());

        await Assert.ThrowsAsync<AppendOnlyViolationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Another_tenant_cannot_read_the_audit_log()
    {
        await SeedWidget();

        Assert.Single(await Rows(tenantId: 1));
        Assert.Empty(await Rows(tenantId: 2));
    }

    [Fact]
    public void An_actor_cannot_be_an_empty_principal_id()
    {
        Assert.Throws<ArgumentException>(() => AuditActor.User(Guid.Empty));
        Assert.Throws<ArgumentException>(() => AuditActor.ApiKey(Guid.Empty));
        Assert.Throws<ArgumentException>(() => AuditActor.System(" "));
    }

    [Fact]
    public async Task A_failed_save_leaves_no_audit_row_behind_for_the_retry()
    {
        // Regression (Codex review): rows staged for a failed save stayed in the tracker, so a
        // corrected retry committed a row for the change that failed as well as the real one.
        var widget = await SeedWidget();

        await using (var db = Context())
        {
            var loaded = await db.Widgets.SingleAsync(w => w.Id == widget.Id);
            loaded.Name = "Rejected";

            // Something else in the same save fails AFTER audit has staged: the tenant guard
            // refuses a row labelled for another tenant.
            var bad = new Receipt { TenantId = 2 };
            db.Receipts.Add(bad);
            await Assert.ThrowsAsync<TenantScopeViolationException>(() => db.SaveChangesAsync());

            db.Receipts.Remove(bad);
            loaded.Name = "Accepted";
            await db.SaveChangesAsync();
        }

        var updates = (await Rows()).Where(r => r.Action == AuditAction.Updated).ToList();
        var row = Assert.Single(updates);
        Assert.Equal("Accepted", Changes(row).GetProperty("Name").GetProperty("new").GetString());
        Assert.DoesNotContain("Rejected", row.Changes);
    }

    [Fact]
    public async Task A_detached_update_records_what_the_database_held_not_what_the_caller_supplied()
    {
        // Regression (Codex review): Update() on a detached entity sets its originals equal to the
        // supplied values, so the snapshot showed no change and the write went unaudited.
        var widget = await SeedWidget();

        await using (var db = Context())
        {
            db.Widgets.Update(new Widget
            {
                Id = widget.Id, TenantId = 1, PublicId = widget.PublicId, Name = "Replaced", Secret = "s3cret",
            });
            await db.SaveChangesAsync();
        }

        var row = (await Rows())[1];
        Assert.Equal(AuditAction.Updated, row.Action);
        var name = Changes(row).GetProperty("Name");
        Assert.Equal("Sprocket", name.GetProperty("old").GetString());
        Assert.Equal("Replaced", name.GetProperty("new").GetString());
    }

    [Fact]
    public async Task A_detached_update_with_no_actor_is_refused()
    {
        var widget = await SeedWidget();

        await using var db = Context(noActor: true);
        db.Widgets.Update(new Widget { Id = widget.Id, TenantId = 1, PublicId = widget.PublicId, Name = "Sneaky" });

        await Assert.ThrowsAsync<AuditActorMissingException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task A_detached_delete_records_the_stored_values_not_the_supplied_ones()
    {
        var widget = await SeedWidget();

        await using (var db = Context())
        {
            db.Widgets.Remove(new Widget { Id = widget.Id, TenantId = 1, PublicId = widget.PublicId, Name = "A lie" });
            await db.SaveChangesAsync();
        }

        var row = (await Rows())[1];
        Assert.Equal(AuditAction.Deleted, row.Action);
        Assert.Equal("Sprocket", Changes(row).GetProperty("Name").GetProperty("old").GetString());
        Assert.DoesNotContain("A lie", row.Changes);
    }

    [Fact]
    public async Task A_change_to_a_row_this_context_cannot_read_is_a_concurrency_conflict_and_stages_nothing()
    {
        // A missing row, or another tenant's: there is no truthful "before". Raised as a
        // concurrency conflict — what handlers already turn into a 409 — rather than a 500.
        await using var db = Context();
        db.Widgets.Update(new Widget { Id = Guid.NewGuid(), TenantId = 1, Name = "Ghost" });

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync());
        Assert.Empty(db.ChangeTracker.Entries<AuditEntry>());
    }

    [Fact]
    public async Task A_detached_delete_is_filed_under_the_stored_public_id_not_the_supplied_one()
    {
        // Regression (Codex re-review): the row id came from the caller, so a delete with an
        // omitted public id was filed under nothing at all.
        var widget = await SeedWidget();

        await using (var db = Context())
        {
            db.Widgets.Remove(new Widget { Id = widget.Id, TenantId = 1, PublicId = "" });
            await db.SaveChangesAsync();
        }

        Assert.Equal(widget.PublicId, (await Rows())[1].EntityId);
    }

    [Fact]
    public async Task A_public_id_cannot_be_changed()
    {
        var widget = await SeedWidget();

        await using var db = Context();
        var loaded = await db.Widgets.SingleAsync(w => w.Id == widget.Id);
        loaded.PublicId = "wid_reissued";
        loaded.Name = "Renamed";

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }
}
