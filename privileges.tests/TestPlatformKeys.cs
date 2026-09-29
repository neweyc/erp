using System.Security.Cryptography;
using AppPlatform.Encryption;
using AppPlatform.Platform.Data;

namespace AppPlatform.PrivilegeTests;

/// <summary>
/// ONE platform key-encryption key for every test sharing the privilege database. The first load
/// creates the platform's data key wrapped by it; a test using a different one would be refused at
/// load, exactly as a service started with the wrong key is.
/// </summary>
internal static class TestPlatformKeys
{
    public static readonly KeyEncryptionKey Kek =
        KeyEncryptionKey.FromConfiguration(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), "Test:PlatformKek");

    public static Task<KeyRing> LoadAsync(PlatformDbContext db) => KeyRing.LoadOrCreateAsync(db, Kek, TimeProvider.System);
}
