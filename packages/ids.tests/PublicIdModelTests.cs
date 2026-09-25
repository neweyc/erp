using AppPlatform.Ids;
using Microsoft.EntityFrameworkCore;

namespace AppPlatform.Ids.Tests;

public class Employee : IPublicIdentified
{
    public Guid Id { get; set; }
    public string PublicId { get; set; } = "";
}

public class GoodDbContext : DbContext
{
    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseInMemoryDatabase(nameof(GoodDbContext));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<Employee>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasPublicId("emp");
        });
}

public class MissingIndexDbContext : DbContext
{
    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseInMemoryDatabase(nameof(MissingIndexDbContext));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<Employee>(e => e.HasKey(x => x.Id));
}

public class PublicIdModelTests
{
    [Fact]
    public void A_configured_entity_passes()
        => Assert.Empty(PublicIdModelAssertions.FindEntitiesMissingPublicIdIndex(new GoodDbContext().Model));

    [Fact]
    public void An_entity_without_a_unique_public_id_index_is_reported()
    {
        // Without the index, duplicates are possible and every integration's lookup path
        // is a table scan.
        Assert.Contains("Employee",
            PublicIdModelAssertions.FindEntitiesMissingPublicIdIndex(new MissingIndexDbContext().Model));
    }

    [Fact]
    public void The_column_is_capped_to_the_canonical_length()
    {
        var property = new GoodDbContext().Model
            .FindEntityType(typeof(Employee))!
            .FindProperty(nameof(IPublicIdentified.PublicId))!;

        Assert.Equal("public_id", property.GetColumnName());
        Assert.Equal(34, property.GetMaxLength());
        Assert.False(property.IsNullable);
    }

    [Fact]
    public void The_index_is_unique_and_global_rather_than_per_tenant()
    {
        // Global uniqueness means an id belonging to tenant A and presented by tenant B
        // matches nothing, instead of matching a DIFFERENT row — which is how a leaked id
        // becomes a mix-up.
        var index = Assert.Single(new GoodDbContext().Model
            .FindEntityType(typeof(Employee))!
            .GetIndexes());

        Assert.True(index.IsUnique);
        Assert.Equal([nameof(IPublicIdentified.PublicId)], index.Properties.Select(p => p.Name));
    }
}
