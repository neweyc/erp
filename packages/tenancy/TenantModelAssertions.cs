using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AppPlatform.Tenancy;

/// <summary>
/// Model-shape checks a consuming test suite runs against its own context. They live in
/// the package, not in each test project, so a new service inherits the rules rather than
/// reimplementing them — and so tightening a rule tightens it everywhere at once.
/// </summary>
public static class TenantModelAssertions
{
    /// <summary>
    /// Every tenant-scoped entity must carry an index leading with TenantId. Without one
    /// the query filter is a sequential scan on every read, which looks like a
    /// performance problem and is discovered in production.
    ///
    /// View-mapped entities are exempt: a consumer cannot index a published view, and the
    /// service that owns the underlying table is where that index belongs. Flagging them would
    /// have made this assertion impossible to satisfy for exactly the entities the published-
    /// contract design exists to create.
    /// </summary>
    public static IReadOnlyList<string> FindEntitiesMissingTenantIndex(IModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        return [.. TenantScopedEntities(model)
            .Where(e => e.GetViewName() is null)
            .Where(e => !e.GetIndexes().Any(i => i.Properties[0].Name == nameof(ITenantScoped.TenantId))
                        && (e.FindPrimaryKey()?.Properties[0].Name) != nameof(ITenantScoped.TenantId))
            .Select(e => e.ClrType.Name)
            .Order()];
    }

    /// <summary>
    /// A foreign key between two tenant-scoped entities must include TenantId, so the
    /// database itself refuses a row pointing at another tenant's parent.
    ///
    /// Query filters do not give this. They scope what a query READS; nothing in them
    /// stops an INSERT storing an id that was never read — and an id arriving in a
    /// request body is user input. Resolving every inbound id through the filtered
    /// context is the first defence, this is the one that still holds when that is
    /// forgotten.
    ///
    /// Only applies WITHIN a schema. A cross-schema reference goes through a published
    /// view, and PostgreSQL cannot key to a view — see the returned diagnostics and
    /// docs/architecture.md.
    /// </summary>
    public static IReadOnlyList<string> FindReferencesNotCarryingTenant(IModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var problems = new List<string>();

        foreach (var entity in TenantScopedEntities(model).Where(e => e.GetViewName() is null))
        {
            foreach (var fk in entity.GetForeignKeys())
            {
                if (!typeof(ITenantScoped).IsAssignableFrom(fk.PrincipalEntityType.ClrType)) continue;
                if (fk.Properties.Any(p => p.Name == nameof(ITenantScoped.TenantId))) continue;

                problems.Add(
                    $"{entity.ClrType.Name}.{string.Join('+', fk.Properties.Select(p => p.Name))} " +
                    $"-> {fk.PrincipalEntityType.ClrType.Name} does not carry TenantId");
            }
        }

        return [.. problems.Order()];
    }

    private static IEnumerable<IEntityType> TenantScopedEntities(IModel model)
        => model.GetEntityTypes()
            .Where(e => !e.IsOwned() && typeof(ITenantScoped).IsAssignableFrom(e.ClrType));
}
