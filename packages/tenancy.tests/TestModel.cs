using AppPlatform.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace AppPlatform.Tenancy.Tests;

public class Widget : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public int TenantId { get; set; }
    public string Name { get; set; } = "";
}

public class Gadget : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public int TenantId { get; set; }
    public Guid WidgetId { get; set; }
}

/// <summary>Not tenant-scoped — a host-level table, to prove the filter leaves it alone.</summary>
public class Region
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class TestDbContext(DbContextOptions options, ITenantProvider provider)
    : TenantedDbContext(options, provider)
{
    public DbSet<Widget> Widgets => Set<Widget>();
    public DbSet<Gadget> Gadgets => Set<Gadget>();
    public DbSet<Region> Regions => Set<Region>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Widget>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasAlternateKey(x => new { x.TenantId, x.Id });
            e.HasIndex(x => x.TenantId);
        });

        modelBuilder.Entity<Gadget>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TenantId);
            // The composite reference: (TenantId, WidgetId) -> (TenantId, Id). A gadget
            // cannot point at another tenant's widget because the database will not
            // allow the row, whatever the application code believes.
            e.HasOne<Widget>()
                .WithMany()
                .HasForeignKey(x => new { x.TenantId, x.WidgetId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id });
        });

        modelBuilder.Entity<Region>(e => e.HasKey(x => x.Id));

        base.OnModelCreating(modelBuilder);
    }
}

/// <summary>A deliberately wrong model, so the assertions are proved to FAIL on bad input.</summary>
public class BadModelDbContext(DbContextOptions options, ITenantProvider provider)
    : TenantedDbContext(options, provider)
{
    public DbSet<Widget> Widgets => Set<Widget>();
    public DbSet<Gadget> Gadgets => Set<Gadget>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Widget>(e => e.HasKey(x => x.Id));
        modelBuilder.Entity<Gadget>(e =>
        {
            e.HasKey(x => x.Id);
            // Tenant-blind reference — exactly the mistake the assertion exists to catch.
            e.HasOne<Widget>().WithMany().HasForeignKey(x => x.WidgetId);
        });

        base.OnModelCreating(modelBuilder);
    }
}
