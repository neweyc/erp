using AppPlatform.Boundary;
using AppPlatform.Core.Data;
using AppPlatform.Ids;
using AppPlatform.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace AppPlatform.BoundaryTests;

/// <summary>
/// The rules applied to a real service model, rather than to a fixture. Everything else in this
/// suite proves the checks work; this is the check actually doing its job.
/// </summary>
public class CoreBoundaryTests
{
    private static CoreDbContext Context()
    {
        var tenant = new AmbientTenantProvider();
        tenant.UseTenant(1);

        return new CoreDbContext(
            new DbContextOptionsBuilder<CoreDbContext>()
                // Npgsql rather than InMemory: the model carries filtered indexes and composite
                // alternate keys that the InMemory provider silently ignores, so the shape
                // under test would not be the shape that ships.
                .UseNpgsql("Host=localhost;Database=unused")
                .Options,
            tenant);
    }

    [Fact]
    public void Core_maps_only_the_schemas_it_is_entitled_to()
    {
        var service = BoundaryRegistry.Services.Single(s => s.ProjectDirectory == "core.api");

        Assert.Empty(SchemaBoundary.FindEntitiesOutsideSchemas(
            Context().Model, [.. service.OwnSchemas, .. service.ReadableViewSchemas]));
    }

    [Fact]
    public void Core_maps_exactly_one_table_from_the_platform_schema()
    {
        // The single deliberate exception, pinned. A second platform table appearing here is a
        // design decision, and this test is where it has to be argued for.
        var platformTables = Context().Model.GetEntityTypes()
            .Where(e => e.GetSchema() == "platform")
            .Select(e => e.GetTableName() ?? "(unnamed)")
            .Order()
            .ToArray();

        Assert.Equal(["tenant"], platformTables);
    }

    [Fact]
    public void Every_tenant_scoped_entity_is_indexed_and_references_carry_the_tenant()
    {
        var model = Context().Model;

        Assert.Empty(TenantModelAssertions.FindEntitiesMissingTenantIndex(model));
        Assert.Empty(TenantModelAssertions.FindReferencesNotCarryingTenant(model));
    }

    [Fact]
    public void Every_externally_referenceable_entity_has_a_unique_public_id()
        => Assert.Empty(PublicIdModelAssertions.FindEntitiesMissingPublicIdIndex(Context().Model));

    [Fact]
    public void Core_source_never_names_another_services_schema()
    {
        // The only check that sees raw SQL, which the model checks are blind to.
        var hits = SourceScan.FindBannedStrings(
            RepositoryPaths.Project("core.api"),
            ["tickets.", "platform.tenant_app", "platform.audit_log"]);

        Assert.Empty(hits);
    }
}
