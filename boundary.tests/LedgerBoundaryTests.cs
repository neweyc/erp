using AppPlatform.Audit;
using AppPlatform.Boundary;
using AppPlatform.Ids;
using AppPlatform.Ledger.Data;
using AppPlatform.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AppPlatform.BoundaryTests;

/// <summary>The ledger's model against the same rules as every other service, plus its own.</summary>
public class LedgerBoundaryTests
{
    private static LedgerDbContext Context()
    {
        var tenant = new AmbientTenantProvider();
        tenant.UseTenant(1);

        return new LedgerDbContext(
            new DbContextOptionsBuilder<LedgerDbContext>()
                .UseNpgsql("Host=unused").UseSnakeCaseNamingConvention().Options,
            tenant, new AmbientAuditActor(), TimeProvider.System);
    }

    private static IModel Model() => Context().Model;

    private static ServiceBoundary Boundary()
        => BoundaryRegistry.Services.Single(s => s.ProjectDirectory == "apps/ledger/ledger.api");

    [Fact]
    public void Ledger_maps_only_its_own_schema_and_the_published_view()
    {
        var service = Boundary();

        Assert.Empty(SchemaBoundary.FindEntitiesOutsideSchemas(
            Model(), [.. service.OwnSchemas, .. service.ReadableViewSchemas]));
    }

    [Fact]
    public void The_published_company_is_mapped_as_a_view_never_a_table()
        => Assert.Empty(SchemaBoundary.FindWritableMappingsInPublishedSchemas(
            Model(), BoundaryRegistry.PublishedSchemas));

    [Fact]
    public void Every_tenant_scoped_entity_is_indexed_and_carries_the_tenant_on_references()
    {
        var model = Model();

        Assert.Empty(TenantModelAssertions.FindEntitiesMissingTenantIndex(model));
        Assert.Empty(TenantModelAssertions.FindReferencesNotCarryingTenant(model));
    }

    [Fact]
    public void Every_piece_of_tenant_data_the_outside_world_can_name_is_audited()
    {
        using var context = Context();

        Assert.Empty(AuditModelAssertions.FindEntitiesThatShouldBeAudited(context.Model));
        Assert.Empty(AuditModelAssertions.FindAuditableEntitiesInUnauditedContext(context));
        Assert.Empty(AuditModelAssertions.FindUnsupportedAuditableShapes(context.Model));
    }

    [Fact]
    public void Every_externally_referenceable_entity_has_a_unique_public_id()
        => Assert.Empty(PublicIdModelAssertions.FindEntitiesMissingPublicIdIndex(Model()));

    [Fact]
    public void Posted_entries_and_their_lines_are_append_only()
    {
        // Pinned: the correction model of the whole app rests on these two never being editable.
        Assert.True(typeof(IAppendOnly).IsAssignableFrom(typeof(JournalEntry)));
        Assert.True(typeof(IAppendOnly).IsAssignableFrom(typeof(JournalLine)));
    }

    [Fact]
    public void An_entry_is_reversed_at_most_once_and_numbered_uniquely_at_the_database()
    {
        var entry = Model().FindEntityType(typeof(JournalEntry))!;

        Assert.Contains(entry.GetIndexes(), i => i.IsUnique
            && i.Properties.Select(p => p.Name).SequenceEqual(
                [nameof(JournalEntry.TenantId), nameof(JournalEntry.ReversesEntryId)]));

        Assert.Contains(entry.GetIndexes(), i => i.IsUnique
            && i.Properties.Select(p => p.Name).SequenceEqual(
                [nameof(JournalEntry.TenantId), nameof(JournalEntry.CompanyId),
                 nameof(JournalEntry.FiscalYear), nameof(JournalEntry.Number)]));
    }
}
