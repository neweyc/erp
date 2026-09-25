using AppPlatform.Audit;
using AppPlatform.Boundary;
using AppPlatform.Ids;
using AppPlatform.Tenancy;
using AppPlatform.Tickets.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AppPlatform.BoundaryTests;

/// <summary>
/// Tickets is the case the whole published-view design exists for: it needs employees, has no
/// grant on core's tables, and must reach them only through the contract.
/// </summary>
public class TicketsBoundaryTests
{
    private static IModel Model() => Context().Model;

    private static TicketsDbContext Context()
    {
        var tenant = new AmbientTenantProvider();
        tenant.UseTenant(1);

        return new TicketsDbContext(
            new DbContextOptionsBuilder<TicketsDbContext>()
                .UseNpgsql("Host=unused").UseSnakeCaseNamingConvention().Options,
            tenant, new AmbientAuditActor(), TimeProvider.System);
    }

    private static ServiceBoundary Boundary()
        => BoundaryRegistry.Services.Single(s => s.ProjectDirectory == "apps/tickets/tickets.api");

    [Fact]
    public void Tickets_maps_only_its_own_schema_and_the_published_view()
    {
        var service = Boundary();

        Assert.Empty(SchemaBoundary.FindEntitiesOutsideSchemas(
            Model(), [.. service.OwnSchemas, .. service.ReadableViewSchemas]));
    }

    [Fact]
    public void The_published_employee_is_mapped_as_a_view_never_a_table()
    {
        // Mapped as a table, this app would hold a write path into data it does not own — and
        // the write would fail at the grant in production rather than here.
        Assert.Empty(SchemaBoundary.FindWritableMappingsInPublishedSchemas(
            Model(), BoundaryRegistry.PublishedSchemas));
    }

    [Fact]
    public void Tickets_reaches_core_only_through_core_v1()
    {
        var coreTables = Model().GetEntityTypes()
            .Where(e => e.GetSchema() == "core" || e.GetSchema() == "identity")
            .Select(e => e.GetTableName() ?? "(unnamed)")
            .ToArray();

        Assert.Empty(coreTables);
    }

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
    public void The_ticket_has_a_unique_public_id()
        => Assert.Empty(PublicIdModelAssertions.FindEntitiesMissingPublicIdIndex(Model()));

    [Fact]
    public void The_assignee_has_no_foreign_key_because_postgres_cannot_key_to_a_view()
    {
        var ticket = Model().GetEntityTypes().Single(e => e.ClrType == typeof(Ticket));

        // Not an oversight — an unavoidable consequence of the boundary, which is why
        // resolve-never-trust plus the stored snapshot carry the weight instead.
        Assert.DoesNotContain(ticket.GetForeignKeys(),
            fk => fk.Properties.Any(p => p.Name == nameof(Ticket.AssigneeEmployeeId)));
    }
}
