using AppPlatform.Auth;
using AppPlatform.Encryption;
using AppPlatform.Platform;
using AppPlatform.Platform.Auth;
using AppPlatform.Platform.Data;
using Microsoft.EntityFrameworkCore;

namespace AppPlatform.PrivilegeTests;

/// <summary>
/// The operator bootstrap command, against the real schema.
///
/// Against real PostgreSQL rather than a fake context because the command's behaviour depends on a
/// uniqueness check and on rows landing in `platform_user` and `platform_audit_log` as the shipped
/// migration defines them — which is exactly what a hand-written stand-in got wrong three times.
/// </summary>
[Collection(nameof(PrivilegeCollection))]
public class CreatePlatformUserTests(PrivilegeFixture fixture)
{
    private const string ValidPassword = "a sufficiently long password";

    private PlatformDbContext Open()
        => new(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(fixture.ConnectionString).UseSnakeCaseNamingConvention().Options);

    private static string UniqueEmail() => $"op-{Guid.NewGuid():N}@e2e.test";

    private static Task<KeyRing> KeysAsync(PlatformDbContext db) => TestPlatformKeys.LoadAsync(db);

    private async Task<(int ExitCode, string Message)> CreateAsync(string? email, string? password)
    {
        await using var db = Open();
        return await CreatePlatformUser.CreateAsync(db, await KeysAsync(db), email, password, TimeProvider.System);
    }

    [Fact]
    public async Task A_new_operator_is_created_with_a_verifiable_password_and_an_audit_row()
    {
        var email = UniqueEmail();
        var (exitCode, message) = await CreateAsync(email, ValidPassword);

        Assert.Equal(0, exitCode);
        Assert.Contains("created", message, StringComparison.Ordinal);

        await using var db = Open();
        var user = await db.PlatformUsers.SingleAsync(u => u.Email == email);
        Assert.StartsWith("op_", user.PublicId, StringComparison.Ordinal);
        Assert.True(PasswordHasher.Verify(ValidPassword, user.PasswordHash));
        Assert.True(user.Active);
        // Enrolled at creation: the secret printed once is the one stored, encrypted, and the
        // stored column holds no trace of it in plaintext.
        var printed = SecretIn(message);
        Assert.Equal(printed, OperatorMfa.ReadSecret(user, await KeysAsync(db)));
        Assert.StartsWith("enc:v1:", user.TotpSecretEncrypted, StringComparison.Ordinal);
        Assert.DoesNotContain(printed, user.TotpSecretEncrypted!, StringComparison.Ordinal);

        Assert.Single(await db.AuditLogs.Where(a => a.Detail == email).ToListAsync());
    }

    [Fact]
    public async Task A_rerun_does_not_change_an_existing_operators_password()
    {
        var email = UniqueEmail();
        var (_, created) = await CreateAsync(email, ValidPassword);
        await using var db = Open();
        var original = await db.PlatformUsers.AsNoTracking().SingleAsync(u => u.Email == email);

        var (exitCode, message) = await CreateAsync(email, "an entirely different password");

        // THE security property. This command can be re-run by anyone who can run it once, so a
        // re-run that silently reset credentials would be a way to take over an operator account
        // without knowing the current password.
        Assert.Equal(0, exitCode);
        Assert.Contains("already exists", message, StringComparison.Ordinal);

        var after = await db.PlatformUsers.AsNoTracking().SingleAsync(u => u.Email == email);
        Assert.Equal(original.PasswordHash, after.PasswordHash);
        // Nor the authenticator, and it does not print a secret: a re-run is not a reset.
        Assert.Equal(original.TotpSecretEncrypted, after.TotpSecretEncrypted);
        Assert.DoesNotContain(CreatePlatformUser.SecretLinePrefix, message, StringComparison.Ordinal);
        Assert.Contains(CreatePlatformUser.SecretLinePrefix, created, StringComparison.Ordinal);
        Assert.True(PasswordHasher.Verify(ValidPassword, after.PasswordHash));
        Assert.False(PasswordHasher.Verify("an entirely different password", after.PasswordHash));

        // And no second audit row, which would imply an account was created twice.
        Assert.Single(await db.AuditLogs.Where(a => a.Detail == email).ToListAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_missing_email_is_refused(string? email)
    {
        var (exitCode, message) = await CreateAsync(email, ValidPassword);

        Assert.Equal(1, exitCode);
        Assert.Contains("Usage", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("elevenchars")]
    public async Task A_password_below_the_minimum_is_refused_and_creates_nothing(string? password)
    {
        var email = UniqueEmail();

        var (exitCode, message) = await CreateAsync(email, password);

        Assert.Equal(1, exitCode);
        Assert.Contains($"{CreatePlatformUser.MinimumPasswordLength} characters", message, StringComparison.Ordinal);
        await using var db = Open();
        Assert.Empty(await db.PlatformUsers.Where(u => u.Email == email).ToListAsync());
    }

    [Fact]
    public async Task The_email_is_normalised_so_case_cannot_create_a_second_operator()
    {
        var email = UniqueEmail();

        await CreateAsync(email.ToUpperInvariant(), ValidPassword);
        var (_, message) = await CreateAsync(email, ValidPassword);

        // Two operator accounts differing only by case would both be able to sign in while the
        // unique index believes they are distinct.
        Assert.Contains("already exists", message, StringComparison.Ordinal);
        await using var db = Open();
        Assert.Single(await db.PlatformUsers.Where(u => u.Email == email).ToListAsync());
    }

    [Fact]
    public async Task A_reset_replaces_the_secret_and_revokes_every_session_in_one_save()
    {
        var email = UniqueEmail();
        var (_, created) = await CreateAsync(email, ValidPassword);
        await using var db = Open();
        var user = await db.PlatformUsers.SingleAsync(u => u.Email == email);
        var now = DateTimeOffset.UtcNow;
        db.PlatformSessions.AddRange(
            new PlatformSession { PlatformUserId = user.Id, CreatedAt = now, LastSeenAt = now, AbsoluteExpiry = now.AddHours(8) },
            new PlatformSession { PlatformUserId = user.Id, CreatedAt = now, LastSeenAt = now, AbsoluteExpiry = now.AddHours(8) });
        user.TotpLastUsedStep = 123;
        await db.SaveChangesAsync();

        await using var resetDb = Open();
        var (exitCode, message) = await ResetPlatformUserMfa.ResetAsync(resetDb, await KeysAsync(resetDb), email, TimeProvider.System);

        Assert.Equal(0, exitCode);
        await using var check = Open();
        var after = await check.PlatformUsers.SingleAsync(u => u.Email == email);
        var newSecret = SecretIn(message);
        Assert.NotEqual(SecretIn(created), newSecret);
        Assert.Equal(newSecret, OperatorMfa.ReadSecret(after, await KeysAsync(check)));
        // The new secret's codes have never been used.
        Assert.Null(after.TotpLastUsedStep);
        // A reset may mean the old phone is in someone else's hands: nothing signed in with it survives.
        Assert.All(await check.PlatformSessions.Where(s => s.PlatformUserId == after.Id).ToListAsync(),
            s => Assert.NotNull(s.RevokedAt));
        Assert.Single(await check.AuditLogs.Where(a => a.Action == "operator.mfa_reset" && a.Detail!.StartsWith(email)).ToListAsync());
    }

    [Fact]
    public async Task A_reset_for_an_unknown_operator_changes_nothing()
    {
        await using var db = Open();

        var (exitCode, message) = await ResetPlatformUserMfa.ResetAsync(db, await KeysAsync(db), UniqueEmail(), TimeProvider.System);

        Assert.Equal(1, exitCode);
        Assert.Contains("no operator", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Of_many_simultaneous_sign_ins_with_one_code_exactly_one_gets_a_session()
    {
        // The replay guard's real-SQL half: one conditional UPDATE, so the check and the write
        // cannot be separated, with the session inserted in the same transaction. A read-then-write
        // version lets every racer see the old step.
        var email = UniqueEmail();
        await CreateAsync(email, ValidPassword);
        PlatformUser user;
        await using (var db = Open()) user = await db.PlatformUsers.AsNoTracking().SingleAsync(u => u.Email == email);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = Open();
            return await new EFOperatorSessionStore(db).CreateSessionWithCodeAsync(
                user.Id, user.TotpSecretVersion, step: 1000, DateTimeOffset.UtcNow);
        }));

        Assert.Single(attempts, id => id is not null);
        await using var check = Open();
        Assert.Single(await check.PlatformSessions.Where(s => s.PlatformUserId == user.Id).ToListAsync());

        var store = new EFOperatorSessionStore(check);
        Assert.Null(await store.CreateSessionWithCodeAsync(user.Id, user.TotpSecretVersion, 999, DateTimeOffset.UtcNow));
        Assert.NotNull(await store.CreateSessionWithCodeAsync(user.Id, user.TotpSecretVersion, 1001, DateTimeOffset.UtcNow));
        Assert.Null(await store.CreateSessionWithCodeAsync(user.Id, user.TotpSecretVersion + 1, 1002, DateTimeOffset.UtcNow));
    }

    private static string SecretIn(string message)
        => message.Split('\n').Single(l => l.StartsWith(CreatePlatformUser.SecretLinePrefix, StringComparison.Ordinal))
            [CreatePlatformUser.SecretLinePrefix.Length..];
}
