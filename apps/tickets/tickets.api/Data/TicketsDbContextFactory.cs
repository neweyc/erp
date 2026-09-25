using AppPlatform.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AppPlatform.Tickets.Data;

/// <summary>Design-time only. See core's equivalent for why Program is not booted here.</summary>
public class TicketsDbContextFactory : IDesignTimeDbContextFactory<TicketsDbContext>
{
    public TicketsDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<TicketsDbContext>()
            .UseNpgsql("Host=design-time;Database=appplatform", npgsql => npgsql
                .MigrationsHistoryTable("__ef_migrations_history", TicketsDbContext.Schema))
            .UseSnakeCaseNamingConvention()
            .Options,
            new AmbientTenantProvider());
}
