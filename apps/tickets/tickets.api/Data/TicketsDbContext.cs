using AppPlatform.Audit;
using AppPlatform.Ids;
using AppPlatform.Outbox;
using AppPlatform.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace AppPlatform.Tickets.Data;

public class TicketsDbContext(
    DbContextOptions<TicketsDbContext> options, ITenantProvider tenant, IAuditActor auditActor, TimeProvider clock)
    : AuditedDbContext(options, tenant, auditActor, clock)
{
    public const string Schema = "tickets";
    public const string PublishedSchema = "core_v1";

    protected override string AuditSchema => Schema;

    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<PublishedEmployee> Employees => Set<PublishedEmployee>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Ticket>(e =>
        {
            e.ToTable("ticket", Schema);
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(4000);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.AssigneeDisplayName).HasMaxLength(201);
            e.HasPublicId("tkt");

            // Version is the optimistic concurrency token, not just an event sequence number.
            //
            // Without this, two concurrent changes read the same version, both increment to the
            // same value, and the SECOND one fails on the outbox's unique
            // (tenant, aggregate, version) index — surfacing as a 500 from a constraint whose
            // name means nothing to the caller. As a token, the losing save fails as a
            // concurrency conflict that the handler can turn into a 409.
            e.Property(x => x.Version).IsConcurrencyToken();

            e.HasIndex(x => x.TenantId);
            e.HasIndex(x => new { x.TenantId, x.Status });

            // NO foreign key to the assignee. PostgreSQL cannot key to a view, and this app has
            // no grant on the table behind it — so cross-schema references are guarded by
            // resolve-never-trust plus the stored snapshot, not by the database.
            // See docs/architecture.md.
        });

        modelBuilder.Entity<PublishedEmployee>(e =>
        {
            // ToView, never ToTable: this is core's published contract. Mapping it as a table would
            // hand this app a write path into data it does not own, and EF would try to migrate it —
            // which would fail anyway, since the migration role has no rights in that schema.
            e.ToView("employee", PublishedSchema);
            e.HasKey(x => x.Id);
        });

        modelBuilder.AddOutbox(Schema);

        base.OnModelCreating(modelBuilder);
    }
}
