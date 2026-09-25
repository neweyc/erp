using AppPlatform.Auth;
using AppPlatform.Platform;
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

    [Fact]
    public async Task A_new_operator_is_created_with_a_verifiable_password_and_an_audit_row()
    {
        var email = UniqueEmail();
        await using var db = Open();

        var (exitCode, message) = await CreatePlatformUser.CreateAsync(
            db, email, ValidPassword, TimeProvider.System);

        Assert.Equal(0, exitCode);
        Assert.Contains("created", message, StringComparison.Ordinal);

        var user = await db.PlatformUsers.SingleAsync(u => u.Email == email);
        Assert.StartsWith("op_", user.PublicId, StringComparison.Ordinal);
        Assert.True(PasswordHasher.Verify(ValidPassword, user.PasswordHash));
        Assert.True(user.Active);
        // MFA is mandatory by design but enrolment is not built yet (M2), so a new operator has
        // no secret. Asserted so that changing it is a deliberate act.
        Assert.Null(user.TotpSecret);

        Assert.Single(await db.AuditLogs.Where(a => a.Detail == email).ToListAsync());
    }

    [Fact]
    public async Task A_rerun_does_not_change_an_existing_operators_password()
    {
        var email = UniqueEmail();
        await using var db = Open();

        await CreatePlatformUser.CreateAsync(db, email, ValidPassword, TimeProvider.System);
        var originalHash = (await db.PlatformUsers.SingleAsync(u => u.Email == email)).PasswordHash;

        var (exitCode, message) = await CreatePlatformUser.CreateAsync(
            db, email, "an entirely different password", TimeProvider.System);

        // THE security property. This command can be re-run by anyone who can run it once, so a
        // re-run that silently reset credentials would be a way to take over an operator account
        // without knowing the current password.
        Assert.Equal(0, exitCode);
        Assert.Contains("already exists", message, StringComparison.Ordinal);

        var after = await db.PlatformUsers.SingleAsync(u => u.Email == email);
        Assert.Equal(originalHash, after.PasswordHash);
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
        await using var db = Open();

        var (exitCode, message) = await CreatePlatformUser.CreateAsync(
            db, email, ValidPassword, TimeProvider.System);

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
        await using var db = Open();

        var (exitCode, message) = await CreatePlatformUser.CreateAsync(
            db, email, password, TimeProvider.System);

        Assert.Equal(1, exitCode);
        Assert.Contains($"{CreatePlatformUser.MinimumPasswordLength} characters", message, StringComparison.Ordinal);
        Assert.Empty(await db.PlatformUsers.Where(u => u.Email == email).ToListAsync());
    }

    [Fact]
    public async Task The_email_is_normalised_so_case_cannot_create_a_second_operator()
    {
        var email = UniqueEmail();
        await using var db = Open();

        await CreatePlatformUser.CreateAsync(db, email.ToUpperInvariant(), ValidPassword, TimeProvider.System);
        var (_, message) = await CreatePlatformUser.CreateAsync(db, email, ValidPassword, TimeProvider.System);

        // Two operator accounts differing only by case would both be able to sign in while the
        // unique index believes they are distinct.
        Assert.Contains("already exists", message, StringComparison.Ordinal);
        Assert.Single(await db.PlatformUsers.Where(u => u.Email == email).ToListAsync());
    }
}
