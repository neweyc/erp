using AppPlatform.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace AppPlatform.Encryption;

/// <summary>
/// A data key, stored WRAPPED: encrypted by the key-encryption key, never in plaintext. Each
/// service keeps its own in its own schema's <c>data_key</c> table, as it keeps its own outbox and
/// audit log.
///
/// Append-only. Deleting one makes everything encrypted under it unreadable, permanently; that is
/// the point of crypto-shredding, and it is not something the application's runtime role gets to
/// do by accident. The key is (Id, KekFingerprint), so rotating the key-encryption key later ADDS a
/// row wrapping the same data key under the new one, and edits nothing.
///
/// Today one set of data keys serves the whole service (the platform's operator secrets). A data
/// key per TENANT, the agreed design for customer data, arrives with the first encrypted tenant
/// field: it adds a tenant column here and a lookup by tenant, and changes nothing below.
/// </summary>
public class DataKey : IAppendOnly
{
    /// <summary>Appears in every value encrypted under this key, so decryption knows which to use.</summary>
    public Guid Id { get; set; }

    /// <summary><see cref="KeyEncryptionKey.Fingerprint"/> of the key that wrapped this row.</summary>
    public required string KekFingerprint { get; set; }

    /// <summary>nonce (12) | ciphertext (32) | tag (16), under the key-encryption key.</summary>
    public required byte[] WrappedKey { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public static class DataKeyModelExtensions
{
    /// <summary>Maps <see cref="DataKey"/> to <c>&lt;schema&gt;.data_key</c>.</summary>
    public static ModelBuilder AddDataKeys(this ModelBuilder modelBuilder, string schema)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<DataKey>(e =>
        {
            e.ToTable("data_key", schema);
            e.HasKey(x => new { x.Id, x.KekFingerprint });
            e.Property(x => x.KekFingerprint).HasMaxLength(16);
        });

        return modelBuilder;
    }
}
