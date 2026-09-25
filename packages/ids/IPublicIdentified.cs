using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AppPlatform.Ids;

/// <summary>
/// Marks an entity that is referenced from outside — an API response, a webhook payload,
/// an export, a URL.
/// </summary>
public interface IPublicIdentified
{
    /// <summary>The canonical text form, e.g. <c>emp_7h2k...</c>. Assigned once, never reissued.</summary>
    string PublicId { get; set; }
}

public static class PublicIdModelExtensions
{
    /// <summary>
    /// Configures the column and its unique index.
    ///
    /// Unique GLOBALLY, not per tenant. Two tenants cannot then hold the same public id, so
    /// an id belonging to tenant A and presented by tenant B finds nothing — rather than
    /// matching a different row, which is how a leak turns into a mix-up.
    /// </summary>
    public static void HasPublicId<T>(this EntityTypeBuilder<T> builder, string prefix)
        where T : class, IPublicIdentified
    {
        ArgumentNullException.ThrowIfNull(builder);
        PublicIdPrefix.Validate(prefix);

        builder.Property(e => e.PublicId)
            .HasColumnName("public_id")
            // Prefix + separator + 26-character body. Capped rather than unbounded so a
            // malformed value fails at the INSERT instead of being stored and puzzled over.
            .HasMaxLength(PublicIdPrefix.MaxLength + 1 + Crockford.BodyLength)
            .IsRequired();

        builder.HasIndex(e => e.PublicId).IsUnique();
    }
}

public static class PublicIdModelAssertions
{
    /// <summary>
    /// Every externally-referenceable entity must carry a unique index on its public id.
    /// Without one, duplicates are possible, and a lookup by public id is a table scan on
    /// the path every integration uses.
    /// </summary>
    public static IReadOnlyList<string> FindEntitiesMissingPublicIdIndex(IModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        return [.. model.GetEntityTypes()
            .Where(e => !e.IsOwned() && typeof(IPublicIdentified).IsAssignableFrom(e.ClrType))
            .Where(e => !e.GetIndexes().Any(i =>
                i.IsUnique
                && i.Properties.Count == 1
                && i.Properties[0].Name == nameof(IPublicIdentified.PublicId)))
            .Select(e => e.ClrType.Name)
            .Order()];
    }
}
