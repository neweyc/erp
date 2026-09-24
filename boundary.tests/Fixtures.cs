using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AppPlatform.BoundaryTests;

public class Ticket
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
}

public class PublishedEmployee
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = "";
}

/// <summary>A well-behaved app: own schema for its tables, a published view it reads.</summary>
public class GoodAppDbContext : DbContext
{
    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseInMemoryDatabase(nameof(GoodAppDbContext));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Ticket>(e =>
        {
            e.HasKey(x => x.Id);
            e.ToTable("ticket", "tickets");
        });

        modelBuilder.Entity<PublishedEmployee>(e =>
        {
            e.HasKey(x => x.Id);
            e.ToView("employee", "core_v1");
        });
    }
}

/// <summary>Three violations at once, so each rule can be shown to catch its own.</summary>
public class BadAppDbContext : DbContext
{
    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseInMemoryDatabase(nameof(BadAppDbContext));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // 1. Reaches past the published view into core's own table.
        modelBuilder.Entity<PublishedEmployee>(e =>
        {
            e.HasKey(x => x.Id);
            e.ToTable("employee", "core");
        });

        // 2. No schema named at all, so it lands in `public` where no grant protects it.
        modelBuilder.Entity<Ticket>(e => e.HasKey(x => x.Id));
    }
}

/// <summary>Maps the published contract as a writable table.</summary>
public class WritesPublishedViewDbContext : DbContext
{
    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseInMemoryDatabase(nameof(WritesPublishedViewDbContext));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<PublishedEmployee>(e =>
        {
            e.HasKey(x => x.Id);
            e.ToTable("employee", "core_v1");
        });
}

internal static class Models
{
    public static IModel Of<T>() where T : DbContext, new() => new T().Model;
}
