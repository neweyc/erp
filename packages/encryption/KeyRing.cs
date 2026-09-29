using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AppPlatform.Encryption;

/// <summary>A value that cannot be decrypted: wrong key, altered, moved to another row, or not ours.</summary>
public sealed class FieldDecryptionException(string message) : CryptographicException(message);

/// <summary>
/// A service's unwrapped data keys, held in memory, and the only thing that encrypts and decrypts
/// field values. Loaded once at startup (<see cref="LoadOrCreateAsync"/>) and registered as a
/// singleton; after that, encrypting touches neither the database nor the key-encryption key.
///
/// A value is AES-256-GCM under the newest data key, stored as text:
///
///     enc:v1:&lt;data key id&gt;:&lt;base64url(nonce | ciphertext | tag)&gt;
///
/// The data key id is in the value, so values encrypted under older keys still decrypt after a new
/// key is added. Every value is also BOUND to where it belongs (<c>boundTo</c>, such as
/// "platform_user.totp_secret:&lt;user id&gt;"): the binding is authenticated but not stored, so a
/// value copied onto another row or into another column fails to decrypt. Without it, someone able
/// to write the table could copy one operator's encrypted secret onto another operator's row.
///
/// Encrypted columns cannot be searched, filtered or sorted in SQL. Keep anything queryable in
/// plaintext columns.
/// </summary>
public sealed class KeyRing
{
    private const string Prefix = "enc:v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int DataKeySize = 32;

    private readonly IReadOnlyDictionary<Guid, byte[]> _dataKeys;
    private readonly Guid _currentKeyId;

    private KeyRing(IReadOnlyDictionary<Guid, byte[]> dataKeys, Guid currentKeyId)
    {
        _dataKeys = dataKeys;
        _currentKeyId = currentKeyId;
    }

    public string Encrypt(string plaintext, string boundTo)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentException.ThrowIfNullOrWhiteSpace(boundTo);

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var sealedBytes = new byte[NonceSize + plainBytes.Length + TagSize];

        using (var aes = new AesGcm(_dataKeys[_currentKeyId], TagSize))
        {
            aes.Encrypt(
                nonce,
                plainBytes,
                sealedBytes.AsSpan(NonceSize, plainBytes.Length),
                sealedBytes.AsSpan(NonceSize + plainBytes.Length, TagSize),
                AssociatedData(_currentKeyId, boundTo));
        }

        nonce.CopyTo(sealedBytes, 0);
        return $"{Prefix}{_currentKeyId:N}:{Base64Url.EncodeToString(sealedBytes)}";
    }

    public string Decrypt(string value, string boundTo)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(boundTo);

        // Messages name what failed, never any part of the value.
        if (!value.StartsWith(Prefix, StringComparison.Ordinal))
            throw new FieldDecryptionException("Not an encrypted value (no enc:v1: prefix).");

        var parts = value[Prefix.Length..].Split(':');
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out var keyId))
            throw new FieldDecryptionException("Malformed encrypted value.");

        if (!_dataKeys.TryGetValue(keyId, out var dataKey))
            throw new FieldDecryptionException($"Encrypted under data key {keyId:N}, which this service does not hold.");

        byte[] sealedBytes;
        try
        {
            sealedBytes = Base64Url.DecodeFromChars(parts[1]);
        }
        catch (FormatException)
        {
            throw new FieldDecryptionException("Malformed encrypted value.");
        }

        if (sealedBytes.Length < NonceSize + TagSize)
            throw new FieldDecryptionException("Malformed encrypted value.");

        var cipherLength = sealedBytes.Length - NonceSize - TagSize;
        var plainBytes = new byte[cipherLength];

        try
        {
            using var aes = new AesGcm(dataKey, TagSize);
            aes.Decrypt(
                sealedBytes.AsSpan(0, NonceSize),
                sealedBytes.AsSpan(NonceSize, cipherLength),
                sealedBytes.AsSpan(NonceSize + cipherLength, TagSize),
                plainBytes,
                AssociatedData(keyId, boundTo));
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new FieldDecryptionException(
                "The value does not authenticate: it was altered, or it belongs to a different row or column.");
        }

        return Encoding.UTF8.GetString(plainBytes);
    }

    private static byte[] AssociatedData(Guid keyId, string boundTo)
        => Encoding.UTF8.GetBytes($"{Prefix}{keyId:N}:{boundTo}");

    /// <summary>
    /// Loads every data key the service holds, unwrapping each with <paramref name="kek"/>. On a
    /// service's first start, when the table is empty, it creates the first data key.
    ///
    /// Fails at startup, with a message naming the problem, if any stored data key has no wrapping
    /// under the configured key-encryption key. That is almost always the wrong key configured,
    /// and a service that started anyway would fail on the first encrypted value it read.
    ///
    /// Two processes starting together on an empty table (the API and an operator command, say)
    /// must end up holding the SAME key, or one encrypts a value the other cannot read until it
    /// restarts. So on PostgreSQL the read-and-maybe-create runs under a transaction-scoped
    /// advisory lock per key table: the second process waits, then finds the first one's key. If
    /// it was configured with a DIFFERENT key-encryption key, it then fails at startup like any
    /// other wrong key, and has written nothing. An advisory lock, not a table lock, because
    /// locking the table needs UPDATE or DELETE on it, which the runtime role must not have.
    /// Non-relational providers (the in-memory one used by unit tests) skip the lock.
    ///
    /// Call it on a FRESH context: one with no open transaction (it starts its own) and no unsaved
    /// changes (creating the first key saves the context, and would save those too).
    /// </summary>
    public static async Task<KeyRing> LoadOrCreateAsync(
        DbContext db, KeyEncryptionKey kek, TimeProvider clock, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(kek);
        ArgumentNullException.ThrowIfNull(clock);

        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        if (transaction is not null)
        {
            // Held until the transaction ends, below or on any failure. Keyed by the table, so each
            // service's key table serialises only its own starts.
            var table = db.Model.FindEntityType(typeof(DataKey))!.GetSchemaQualifiedTableName();
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext({0}))", [$"app-platform:{table}"], ct);
        }

        var rows = await db.Set<DataKey>().AsNoTracking().ToListAsync(ct);

        if (rows.Count == 0)
        {
            var created = CreateDataKey(kek, clock.GetUtcNow());
            db.Set<DataKey>().Add(created);
            await db.SaveChangesAsync(ct);
            rows = [created];
        }

        if (transaction is not null) await transaction.CommitAsync(ct);

        var unwrappable = rows
            .GroupBy(r => r.Id)
            .Where(g => g.All(r => r.KekFingerprint != kek.Fingerprint))
            .Select(g => g.Key)
            .ToList();

        if (unwrappable.Count > 0)
        {
            var found = string.Join(", ", rows.Select(r => r.KekFingerprint).Distinct());
            throw new InvalidOperationException(
                $"{unwrappable.Count} data key(s) are not wrapped by the configured key-encryption key " +
                $"(fingerprint {kek.Fingerprint}); stored keys were wrapped by: {found}. The configured " +
                "key is almost certainly the wrong one. Starting anyway would fail on the first encrypted value read.");
        }

        var usable = rows.Where(r => r.KekFingerprint == kek.Fingerprint).ToList();
        var dataKeys = usable.ToDictionary(r => r.Id, r => Unwrap(kek, r));
        var current = usable.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).First();

        return new KeyRing(dataKeys, current.Id);
    }

    internal static DataKey CreateDataKey(KeyEncryptionKey kek, DateTimeOffset now)
    {
        var id = Guid.CreateVersion7();
        var dataKey = RandomNumberGenerator.GetBytes(DataKeySize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var wrapped = new byte[NonceSize + DataKeySize + TagSize];

        using (var aes = new AesGcm(kek.Bytes, TagSize))
        {
            aes.Encrypt(
                nonce, dataKey,
                wrapped.AsSpan(NonceSize, DataKeySize),
                wrapped.AsSpan(NonceSize + DataKeySize, TagSize),
                WrappingAssociatedData(id));
        }

        nonce.CopyTo(wrapped, 0);

        return new DataKey { Id = id, KekFingerprint = kek.Fingerprint, WrappedKey = wrapped, CreatedAt = now };
    }

    private static byte[] Unwrap(KeyEncryptionKey kek, DataKey row)
    {
        if (row.WrappedKey.Length != NonceSize + DataKeySize + TagSize)
            throw new InvalidOperationException($"Data key {row.Id:N} is malformed.");

        var dataKey = new byte[DataKeySize];
        try
        {
            using var aes = new AesGcm(kek.Bytes, TagSize);
            aes.Decrypt(
                row.WrappedKey.AsSpan(0, NonceSize),
                row.WrappedKey.AsSpan(NonceSize, DataKeySize),
                row.WrappedKey.AsSpan(NonceSize + DataKeySize, TagSize),
                dataKey,
                WrappingAssociatedData(row.Id));
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new InvalidOperationException(
                $"Data key {row.Id:N} does not unwrap under the configured key-encryption key although " +
                "its fingerprint matches: the stored row was altered.");
        }

        return dataKey;
    }

    /// <summary>Binds a wrapped key to its own id, so a wrapped key copied onto another row fails.</summary>
    private static byte[] WrappingAssociatedData(Guid dataKeyId) => Encoding.UTF8.GetBytes($"data_key:{dataKeyId:N}");
}
