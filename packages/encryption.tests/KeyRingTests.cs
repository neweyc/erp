using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace AppPlatform.Encryption.Tests;

public class KeyRingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private sealed class KeysContext(DbContextOptions<KeysContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddDataKeys("test");
    }

    /// <summary>One in-memory database per name, so a test can "restart" against the same keys.</summary>
    private static KeysContext Database(string name)
        => new(new DbContextOptionsBuilder<KeysContext>().UseInMemoryDatabase(name).Options);

    private static KeyEncryptionKey NewKek()
        => KeyEncryptionKey.FromConfiguration(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), "Test:Kek");

    private static Task<KeyRing> LoadAsync(KeysContext db, KeyEncryptionKey kek)
        => KeyRing.LoadOrCreateAsync(db, kek, new FakeTimeProvider(Now));

    [Fact]
    public async Task A_value_round_trips()
    {
        var ring = await LoadAsync(Database(nameof(A_value_round_trips)), NewKek());

        var encrypted = ring.Encrypt("JBSWY3DPEHPK3PXP", "platform_user.totp_secret:1");

        Assert.StartsWith("enc:v1:", encrypted, StringComparison.Ordinal);
        Assert.DoesNotContain("JBSWY3DPEHPK3PXP", encrypted, StringComparison.Ordinal);
        Assert.Equal("JBSWY3DPEHPK3PXP", ring.Decrypt(encrypted, "platform_user.totp_secret:1"));
    }

    [Fact]
    public async Task The_same_value_encrypts_differently_every_time()
    {
        // A fresh nonce per value. Reusing a nonce under one GCM key breaks both confidentiality
        // and authentication, and identical ciphertexts would also reveal equal values.
        var ring = await LoadAsync(Database(nameof(The_same_value_encrypts_differently_every_time)), NewKek());

        Assert.NotEqual(ring.Encrypt("same", "t.c:1"), ring.Encrypt("same", "t.c:1"));
    }

    [Theory]
    [InlineData("platform_user.totp_secret:2")] // another row
    [InlineData("platform_user.recovery_code:1")] // another column
    public async Task A_value_moved_to_another_row_or_column_does_not_decrypt(string elsewhere)
    {
        // Someone able to write the table could otherwise copy their own encrypted secret onto
        // another operator's row and then produce that operator's codes.
        var ring = await LoadAsync(Database(nameof(A_value_moved_to_another_row_or_column_does_not_decrypt) + elsewhere), NewKek());
        var encrypted = ring.Encrypt("secret", "platform_user.totp_secret:1");

        Assert.Throws<FieldDecryptionException>(() => ring.Decrypt(encrypted, elsewhere));
    }

    [Fact]
    public async Task An_altered_value_does_not_decrypt()
    {
        var ring = await LoadAsync(Database(nameof(An_altered_value_does_not_decrypt)), NewKek());
        var encrypted = ring.Encrypt("secret", "t.c:1");
        var last = encrypted[^1] == 'A' ? 'B' : 'A';

        Assert.Throws<FieldDecryptionException>(() => ring.Decrypt(encrypted[..^1] + last, "t.c:1"));
    }

    [Theory]
    [InlineData("plaintext")]
    [InlineData("enc:v1:not-a-key-id:AAAA")]
    [InlineData("enc:v1:0123456789abcdef0123456789abcdef")]
    [InlineData("enc:v1:0123456789abcdef0123456789abcdef:***")]
    public async Task Something_that_is_not_one_of_our_values_is_refused_clearly(string value)
    {
        var ring = await LoadAsync(Database(nameof(Something_that_is_not_one_of_our_values_is_refused_clearly) + value), NewKek());

        Assert.Throws<FieldDecryptionException>(() => ring.Decrypt(value, "t.c:1"));
    }

    [Fact]
    public async Task A_value_under_a_data_key_this_service_does_not_hold_is_refused()
    {
        var mine = await LoadAsync(Database("mine"), NewKek());
        var theirs = await LoadAsync(Database("theirs"), NewKek());

        var error = Assert.Throws<FieldDecryptionException>(() => mine.Decrypt(theirs.Encrypt("x", "t.c:1"), "t.c:1"));
        Assert.Contains("does not hold", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_first_start_creates_one_data_key_and_later_starts_reuse_it()
    {
        var name = nameof(The_first_start_creates_one_data_key_and_later_starts_reuse_it);
        var kek = NewKek();

        var first = await LoadAsync(Database(name), kek);
        var encrypted = first.Encrypt("kept", "t.c:1");
        var restarted = await LoadAsync(Database(name), kek);

        Assert.Single(await Database(name).Set<DataKey>().ToListAsync());
        Assert.Equal("kept", restarted.Decrypt(encrypted, "t.c:1"));
    }

    [Fact]
    public async Task The_stored_data_key_is_wrapped_not_plaintext()
    {
        var name = nameof(The_stored_data_key_is_wrapped_not_plaintext);
        var kek = NewKek();
        await LoadAsync(Database(name), kek);

        var row = Assert.Single(await Database(name).Set<DataKey>().ToListAsync());

        Assert.Equal(kek.Fingerprint, row.KekFingerprint);
        Assert.Equal(12 + 32 + 16, row.WrappedKey.Length);
    }

    [Fact]
    public async Task Starting_with_the_wrong_key_encryption_key_fails_at_startup_saying_so()
    {
        var name = nameof(Starting_with_the_wrong_key_encryption_key_fails_at_startup_saying_so);
        var right = NewKek();
        await LoadAsync(Database(name), right);
        var wrong = NewKek();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => LoadAsync(Database(name), wrong));

        Assert.Contains(wrong.Fingerprint, error.Message, StringComparison.Ordinal);
        Assert.Contains(right.Fingerprint, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Values_under_an_older_data_key_still_decrypt_and_new_values_use_the_newest()
    {
        var name = nameof(Values_under_an_older_data_key_still_decrypt_and_new_values_use_the_newest);
        var kek = NewKek();
        var old = await LoadAsync(Database(name), kek);
        var underOld = old.Encrypt("old value", "t.c:1");

        await using (var db = Database(name))
        {
            db.Set<DataKey>().Add(KeyRing.CreateDataKey(kek, Now.AddDays(1)));
            await db.SaveChangesAsync();
        }

        var reloaded = await LoadAsync(Database(name), kek);

        Assert.Equal("old value", reloaded.Decrypt(underOld, "t.c:1"));
        Assert.NotEqual(underOld.Split(':')[2], reloaded.Encrypt("new", "t.c:1").Split(':')[2]);
    }

    [Theory]
    [InlineData(null, "is required")]
    [InlineData("  ", "is required")]
    [InlineData("not base64!", "not valid base64")]
    [InlineData("AAAA", "must decode to 32 bytes")]
    public void A_missing_or_malformed_key_encryption_key_is_refused(string? value, string expected)
    {
        var error = Assert.Throws<InvalidOperationException>(() => KeyEncryptionKey.FromConfiguration(value, "Encryption:Test"));

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
        Assert.Contains("Encryption:Test", error.Message, StringComparison.Ordinal);
    }
}
