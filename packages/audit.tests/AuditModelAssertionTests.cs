using AppPlatform.Ids;
using AppPlatform.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace AppPlatform.Audit.Tests;

/// <summary>Tenant data with a public id that forgot to opt in.</summary>
public class Invoice : ITenantScoped, IPublicIdentified
{
    public Guid Id { get; set; }
    public int TenantId { get; set; }
    public string PublicId { get; set; } = "";
}

/// <summary>
/// An auditable entity in a context that is NOT an AuditedDbContext — and maps the audit table by
/// hand, so a check that only looked for the table in the model would pass it.
/// </summary>
public class UnauditedDbContext(DbContextOptions options, ITenantProvider tenant)
    : TenantedDbContext(options, tenant)
{
    public DbSet<Widget> Widgets => Set<Widget>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddAuditLog("test");
        base.OnModelCreating(modelBuilder);
    }
}

public class Address
{
    public string Street { get; set; } = "";
}

/// <summary>Auditable, with the two shapes audit refuses: an owned type and a generated value.</summary>
public class Depot : IAuditable
{
    public Guid Id { get; set; }
    public int TenantId { get; set; }
    public string PublicId { get; set; } = "";
    public Address Address { get; set; } = new();
    public DateTimeOffset Stamped { get; set; }
}

public class ShapesDbContext(DbContextOptions options, ITenantProvider tenant, IAuditActor actor, TimeProvider clock)
    : AuditedDbContext(options, tenant, actor, clock)
{
    protected override string AuditSchema => "test";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Depot>(e =>
        {
            e.OwnsOne(x => x.Address);
            e.Property(x => x.Stamped).ValueGeneratedOnAdd();
        });
        base.OnModelCreating(modelBuilder);
    }
}

public class ForgetfulDbContext(DbContextOptions options, ITenantProvider tenant, IAuditActor actor, TimeProvider clock)
    : AuditedDbContext(options, tenant, actor, clock)
{
    protected override string AuditSchema => "test";

    public DbSet<Widget> Widgets => Set<Widget>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<Receipt> Receipts => Set<Receipt>();
}

/// <summary>Proves the architecture checks can fail — the boundary suite proves they pass.</summary>
public class AuditModelAssertionTests
{
    private static DbContextOptions Options()
        => new DbContextOptionsBuilder().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    [Fact]
    public void Public_tenant_data_that_is_not_auditable_is_reported()
    {
        using var db = new ForgetfulDbContext(
            Options(), new AmbientTenantProvider(), new AmbientAuditActor(), TimeProvider.System);

        // Invoice is reported; Widget is auditable; Receipt has no public id, so is not tenant
        // data the outside world can name.
        Assert.Equal(["Invoice"], AuditModelAssertions.FindEntitiesThatShouldBeAudited(db.Model));
    }

    [Fact]
    public void An_explicit_exemption_is_honoured()
    {
        using var db = new ForgetfulDbContext(
            Options(), new AmbientTenantProvider(), new AmbientAuditActor(), TimeProvider.System);

        Assert.Empty(AuditModelAssertions.FindEntitiesThatShouldBeAudited(db.Model, typeof(Invoice)));
    }

    [Fact]
    public void An_auditable_entity_in_a_context_that_does_not_stage_audit_is_reported()
    {
        // Even though this context maps the audit table: mapping it is not what writes rows.
        using var db = new UnauditedDbContext(Options(), new AmbientTenantProvider());

        Assert.Equal(["Widget"], AuditModelAssertions.FindAuditableEntitiesInUnauditedContext(db));
    }

    [Fact]
    public void An_audited_context_passes()
    {
        using var db = new ForgetfulDbContext(
            Options(), new AmbientTenantProvider(), new AmbientAuditActor(), TimeProvider.System);

        Assert.Empty(AuditModelAssertions.FindAuditableEntitiesInUnauditedContext(db));
    }

    [Fact]
    public void Owned_types_and_generated_values_on_an_auditable_entity_are_reported()
    {
        using var db = new ShapesDbContext(
            Options(), new AmbientTenantProvider(), new AmbientAuditActor(), TimeProvider.System);

        Assert.Equal(
            ["Depot.Address (owned type)", "Depot.Stamped (database-generated)"],
            AuditModelAssertions.FindUnsupportedAuditableShapes(db.Model));
    }
}
