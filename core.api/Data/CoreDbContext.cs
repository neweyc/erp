using AppPlatform.Ids;
using AppPlatform.Outbox;
using AppPlatform.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace AppPlatform.Core.Data;

/// <summary>
/// Owns the <c>core</c> and <c>identity</c> schemas, plus the outbox in <c>core</c>.
///
/// Maps <see cref="Tenant"/> from the <c>platform</c> schema — the single deliberate exception
/// to the boundary, whitelisted in BoundaryRegistry and enforced at the database by a grant of
/// SELECT and INSERT only.
/// </summary>
public class CoreDbContext(DbContextOptions<CoreDbContext> options, ITenantProvider tenant)
    : TenantedDbContext(options, tenant)
{
    public const string Schema = "core";
    public const string IdentitySchema = "identity";

    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<UserToken> UserTokens => Set<UserToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>(e =>
        {
            // Mapped for reading and for the one INSERT provisioning needs, but EXCLUDED FROM
            // MIGRATIONS: platform.api owns this table's DDL. Without this, core's migration
            // emits CREATE TABLE platform.tenant — which collides with platform's own
            // migration and, sooner, fails outright because ap_core_migrate has no DDL rights
            // on that schema at all.
            //
            // The general rule: a table another service owns is mapped, never migrated.
            e.ToTable("tenant", "platform", t => t.ExcludeFromMigrations());
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ProvisioningKey).HasMaxLength(200);
            e.HasPublicId("ten");
        });

        modelBuilder.Entity<Company>(e =>
        {
            e.ToTable("company", Schema);
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.LegalName).HasMaxLength(200);
            e.HasPublicId("co");
            e.HasIndex(x => x.TenantId);
            e.HasAlternateKey(x => new { x.TenantId, x.Id });
        });

        modelBuilder.Entity<Employee>(e =>
        {
            e.ToTable("employee", Schema);
            e.HasKey(x => x.Id);
            e.Property(x => x.FirstName).HasMaxLength(100);
            e.Property(x => x.LastName).HasMaxLength(100);
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Ignore(x => x.DisplayName);
            e.HasPublicId("emp");

            e.HasIndex(x => x.TenantId);

            // Email is unique per tenant among employees that still exist. Two live records
            // at one address make "invite this employee" ambiguous in a way no UI can resolve.
            e.HasIndex(x => new { x.TenantId, x.Email })
                .IsUnique()
                .HasFilter("email IS NOT NULL AND deleted = false");

            // Carries the tenant, so the database refuses an employee pointed at another
            // tenant's company rather than merely making it unlikely.
            e.HasOne<Company>()
                .WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id });

            e.HasAlternateKey(x => new { x.TenantId, x.Id });
        });

        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("user", IdentitySchema);
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.Role).HasMaxLength(50);
            e.Property(x => x.PasswordHash).HasMaxLength(200);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Ignore(x => x.Active);
            e.HasPublicId("usr");

            e.HasIndex(x => x.TenantId);
            e.HasIndex(x => new { x.TenantId, x.Email }).IsUnique();

            // One account per employee. Without this a second invite to the same person
            // creates a parallel identity, and deactivating one on termination leaves the
            // other working.
            e.HasIndex(x => new { x.TenantId, x.EmployeeId })
                .IsUnique()
                .HasFilter("employee_id IS NOT NULL");

            e.HasOne<Employee>()
                .WithMany()
                .HasForeignKey(x => new { x.TenantId, x.EmployeeId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<UserToken>(e =>
        {
            e.ToTable("user_token", IdentitySchema);
            e.HasKey(x => x.Id);
            e.Property(x => x.Purpose).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.TokenHash).HasMaxLength(64);

            e.HasIndex(x => x.TenantId);
            // Looked up BY HASH, because the plaintext is only ever in the email.
            e.HasIndex(x => x.TokenHash).IsUnique();

            // The token cannot be consumed twice: EF adds used_at to the UPDATE predicate, so a
            // second simultaneous use matches no row instead of both succeeding.
            e.Property(x => x.UsedAt).IsConcurrencyToken();

            e.HasOne<User>()
                .WithMany()
                .HasForeignKey(x => new { x.TenantId, x.UserId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Session>(e =>
        {
            e.ToTable("session", IdentitySchema);
            e.HasKey(x => x.Id);

            e.HasIndex(x => x.TenantId);
            // The revalidation lookup is by session id alone (the cookie carries no tenant), so
            // the primary key already serves it. This index serves revoke-all-for-a-user.
            e.HasIndex(x => new { x.TenantId, x.UserId });

            e.HasOne<User>()
                .WithMany()
                .HasForeignKey(x => new { x.TenantId, x.UserId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.AddOutbox(Schema);

        base.OnModelCreating(modelBuilder);
    }
}
