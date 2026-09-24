using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AppPlatform.Boundary;

/// <summary>
/// The schema whitelist, checked against a live EF model.
///
/// This is a guardrail, not the boundary. The boundary is the PostgreSQL grants in
/// docs/database-privileges.md — a role that cannot SELECT another schema cannot read it
/// whatever the code says, including from raw SQL these checks never see. What this
/// catches is the honest mistake, at build time, with a message naming the entity.
/// </summary>
public static class SchemaBoundary
{
    /// <summary>
    /// Reports entities mapped outside the permitted schemas. A null schema counts as a
    /// violation: relying on the provider's default puts tables in `public`, where the
    /// grants that make the boundary real do not apply.
    /// </summary>
    public static IReadOnlyList<string> FindEntitiesOutsideSchemas(IModel model, params string[] allowed)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(allowed);

        var permitted = new HashSet<string>(allowed, StringComparer.Ordinal);

        return [.. Mapped(model)
            .Select(e => (Entity: e, Schema: SchemaOf(e)))
            .Where(x => x.Schema is null || !permitted.Contains(x.Schema))
            .Select(x => x.Schema is null
                ? $"{x.Entity.ClrType.Name} maps to the default schema; name one explicitly"
                : $"{x.Entity.ClrType.Name} maps to schema '{x.Schema}', which is not in " +
                  $"[{string.Join(", ", permitted.Order())}]")
            .Order()];
    }

    /// <summary>
    /// Reports entities in a published schema (core_v1, identity_v1) mapped as TABLES.
    /// A published contract is read-only: mapping it to a table hands an app a write path
    /// into data it does not own, and the write would fail at the grant — in production,
    /// not here.
    /// </summary>
    public static IReadOnlyList<string> FindWritableMappingsInPublishedSchemas(
        IModel model, IEnumerable<string> publishedSchemas)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(publishedSchemas);

        var published = new HashSet<string>(publishedSchemas, StringComparer.Ordinal);

        return [.. Mapped(model)
            .Where(e => SchemaOf(e) is { } s && published.Contains(s))
            .Where(e => e.GetViewName() is null)
            .Select(e => $"{e.ClrType.Name} is in published schema '{SchemaOf(e)}' but is mapped " +
                         "as a table; use ToView so it cannot be written")
            .Order()];
    }

    private static IEnumerable<IEntityType> Mapped(IModel model)
        => model.GetEntityTypes().Where(e => !e.IsOwned());

    private static string? SchemaOf(IEntityType entity)
        => entity.GetViewSchema() ?? entity.GetSchema();
}
