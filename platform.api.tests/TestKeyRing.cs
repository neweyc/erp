using System.Security.Cryptography;
using AppPlatform.Encryption;
using Microsoft.EntityFrameworkCore;

namespace AppPlatform.Platform.Tests;

/// <summary>A real key ring over a throwaway in-memory key table, for handler tests.</summary>
internal static class TestKeyRing
{
    private sealed class KeysContext(DbContextOptions<KeysContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddDataKeys("test");
    }

    public static KeyRing Create()
    {
        using var db = new KeysContext(new DbContextOptionsBuilder<KeysContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var kek = KeyEncryptionKey.FromConfiguration(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), "Test:Kek");

        return KeyRing.LoadOrCreateAsync(db, kek, TimeProvider.System).GetAwaiter().GetResult();
    }
}
