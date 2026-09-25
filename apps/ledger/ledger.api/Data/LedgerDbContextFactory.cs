using AppPlatform.Audit;
using AppPlatform.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AppPlatform.Ledger.Data;

/// <summary>Design-time only. See core's equivalent for why Program is not booted here.</summary>
public class LedgerDbContextFactory : IDesignTimeDbContextFactory<LedgerDbContext>
{
    public LedgerDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql("Host=design-time;Database=appplatform", npgsql => npgsql
                .MigrationsHistoryTable("__ef_migrations_history", LedgerDbContext.Schema))
            .UseSnakeCaseNamingConvention()
            .Options,
            new AmbientTenantProvider(), new AmbientAuditActor(), TimeProvider.System);
}
