using AppPlatform.Ids;
using Microsoft.EntityFrameworkCore;

namespace AppPlatform.Platform.Data;

/// <summary>
/// Maps the <c>platform</c> schema and NOTHING else.
///
/// The boundary is not this class — it is the Postgres grant that leaves <c>ap_platform_rt</c>
/// with no access to <c>core</c>, <c>identity</c>, or any app schema. This mapping is the
/// guardrail that catches the honest mistake at build time; the grant is what holds against
/// raw SQL and against code that is not in this repo.
///
/// Deliberately NOT a TenantedDbContext: platform tables are host-level. A tenant filter here
/// would be meaningless — the platform's whole job is to see across tenants — and inheriting
/// one would silently scope operator queries to a tenant nobody set.
/// </summary>
public class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    public const string Schema = "platform";

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantApp> TenantApps => Set<TenantApp>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<PlatformUser> PlatformUsers => Set<PlatformUser>();
    public DbSet<PlatformSession> PlatformSessions => Set<PlatformSession>();
    public DbSet<PlatformAuditLog> AuditLogs => Set<PlatformAuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>(e =>
        {
            e.ToTable("tenant", Schema);
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasPublicId("ten");
            e.Property(x => x.ProvisioningKey).HasMaxLength(200);

            // The idempotency guard, enforced by the database rather than by a prior read:
            // two concurrent provisioning calls both find nothing and both proceed, and only
            // this index stops both committing a tenant.
            e.HasIndex(x => x.ProvisioningKey).IsUnique().HasFilter("provisioning_key IS NOT NULL");
        });

        modelBuilder.Entity<TenantApp>(e =>
        {
            e.ToTable("tenant_app", Schema);
            e.HasKey(x => x.Id);
            e.Property(x => x.App).HasMaxLength(50);
            e.Ignore(x => x.IsActive);

            // One live grant per app per tenant. Two active rows would make "is this licensed"
            // depend on which one a query happened to read.
            e.HasIndex(x => new { x.TenantId, x.App })
                .IsUnique()
                .HasFilter("revoked_at IS NULL");

            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId);
        });

        modelBuilder.Entity<IdempotencyRecord>(e =>
        {
            e.ToTable("idempotency_record", Schema);
            e.HasKey(x => x.Id);
            e.Property(x => x.Key).HasMaxLength(200);
            e.Property(x => x.Operation).HasMaxLength(100);
            e.Property(x => x.ResponseJson).HasColumnType("jsonb");

            // THE constraint. Uniqueness is enforced by the database, not by a prior read:
            // two concurrent retries both find nothing, and only the index stops them both
            // provisioning.
            e.HasIndex(x => new { x.Operation, x.Key }).IsUnique();
        });

        modelBuilder.Entity<PlatformUser>(e =>
        {
            e.ToTable("platform_user", Schema);
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.PasswordHash).HasMaxLength(200);
            e.Ignore(x => x.MfaEnabled);
            e.HasPublicId("op");
            e.HasIndex(x => x.Email).IsUnique();

            // The version, not the ciphertext, is the concurrency token: AES-GCM produces
            // different bytes for the same plaintext every time, so comparing the column
            // itself would report a conflict on every write.
            e.Property(x => x.TotpSecretVersion).IsConcurrencyToken();
        });

        modelBuilder.Entity<PlatformSession>(e =>
        {
            e.ToTable("platform_session", Schema);
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.PlatformUserId);
            e.HasOne<PlatformUser>().WithMany().HasForeignKey(x => x.PlatformUserId);
        });

        modelBuilder.Entity<PlatformAuditLog>(e =>
        {
            e.ToTable("audit_log", Schema);
            e.HasKey(x => x.Id);
            e.Property(x => x.Action).HasMaxLength(100);
            e.Property(x => x.TenantPublicId).HasMaxLength(40);
            e.Property(x => x.Detail).HasMaxLength(1000);
            e.HasIndex(x => x.CreatedAt);
        });

        base.OnModelCreating(modelBuilder);
    }
}
