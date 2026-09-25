using AppPlatform.Ids;
using AppPlatform.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AppPlatform.Audit;

/// <summary>
/// Architecture checks run by <c>BoundaryTests</c> against every service model. They make audit
/// the default rather than something each new entity has to remember.
/// </summary>
public static class AuditModelAssertions
{
    /// <summary>
    /// Tenant data that the outside world can name — tenant-scoped with a public id — but that is
    /// not <see cref="IAuditable"/>. An exemption is passed explicitly, so leaving an entity out of
    /// the audit trail is a decision a reviewer sees rather than an omission nobody notices.
    /// </summary>
    public static IReadOnlyList<string> FindEntitiesThatShouldBeAudited(IModel model, params Type[] exempt)
    {
        ArgumentNullException.ThrowIfNull(model);

        return [.. model.GetEntityTypes()
            .Where(e => !e.IsOwned() && e.GetTableName() is not null)
            .Where(e => typeof(ITenantScoped).IsAssignableFrom(e.ClrType)
                && typeof(IPublicIdentified).IsAssignableFrom(e.ClrType))
            .Where(e => !typeof(IAuditable).IsAssignableFrom(e.ClrType))
            .Where(e => !exempt.Contains(e.ClrType))
            .Select(e => e.ClrType.Name)
            .Order()];
    }

    /// <summary>
    /// Auditable entities in a context that does not derive from <see cref="AuditedDbContext"/>.
    /// Checked on the CONTEXT, not the model: a context could map <see cref="AuditEntry"/> by hand
    /// and still never stage a row, since staging lives in the base class's SaveChanges.
    /// </summary>
    public static IReadOnlyList<string> FindAuditableEntitiesInUnauditedContext(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context is AuditedDbContext) return [];

        return [.. context.Model.GetEntityTypes()
            .Where(e => typeof(IAuditable).IsAssignableFrom(e.ClrType))
            .Select(e => e.ClrType.Name)
            .Order()];
    }

    /// <summary>
    /// Shapes <see cref="AuditTrail"/> cannot record truthfully, rejected rather than half-supported:
    /// <list type="bullet">
    /// <item>an owned type — its changes are tracked on a separate entry that is not auditable, so
    /// editing only an owned value would leave no row;</item>
    /// <item>a non-key value the database generates — audit is staged before the save, when that
    /// value does not exist yet, so the row would record a placeholder.</item>
    /// </list>
    /// Supporting either is possible; doing it without a test that needs it is not worth it.
    /// </summary>
    public static IReadOnlyList<string> FindUnsupportedAuditableShapes(IModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var auditable = model.GetEntityTypes()
            .Where(e => typeof(IAuditable).IsAssignableFrom(e.ClrType))
            .ToList();

        var owned = auditable
            .SelectMany(e => e.GetNavigations()
                .Where(n => n.TargetEntityType.IsOwned())
                .Select(n => $"{e.ClrType.Name}.{n.Name} (owned type)"));

        var generated = auditable
            .SelectMany(e => e.GetProperties()
                .Where(p => !p.IsPrimaryKey() && p.ValueGenerated != ValueGenerated.Never)
                .Select(p => $"{e.ClrType.Name}.{p.Name} (database-generated)"));

        return [.. owned.Concat(generated).Order()];
    }
}
