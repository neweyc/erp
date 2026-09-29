using System.Security.Cryptography;
using AppPlatform.Encryption;
using AppPlatform.Platform;
using AppPlatform.Platform.Auth;
using AppPlatform.Platform.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AppPlatform.PrivilegeTests;

/// <summary>
/// The races behind operator MFA and the key ring, against real PostgreSQL, where the locks that
/// decide them actually exist.
///
/// The reset/sign-in tests hold one side open mid-transaction by hand, start the other, check it
/// is genuinely waiting on the operator's row lock, then let the first side commit. That makes each
/// interleaving happen every run rather than occasionally.
/// </summary>
[Collection(nameof(PrivilegeCollection))]
public class OperatorMfaRaceTests(PrivilegeFixture fixture)
{
    /// <summary>Long enough that a statement not blocked on a lock would certainly have finished.</summary>
    private static readonly TimeSpan BlockedFor = TimeSpan.FromMilliseconds(500);

    private PlatformDbContext Open() => PlatformDbContext.ForConnection(fixture.ConnectionString);

    private async Task<PlatformUser> NewOperatorAsync()
    {
        var email = $"op-{Guid.NewGuid():N}@e2e.test";
        await using var db = Open();
        await CreatePlatformUser.CreateAsync(db, await TestPlatformKeys.LoadAsync(db), email, "a sufficiently long password", TimeProvider.System);
        return await db.PlatformUsers.AsNoTracking().SingleAsync(u => u.Email == email);
    }

    [Fact]
    public async Task A_sign_in_that_checked_the_old_secret_gets_no_session_once_a_reset_commits()
    {
        var user = await NewOperatorAsync();

        // The reset side, held open: row locked, secret version moved on, not yet committed.
        await using var reset = new NpgsqlConnection(fixture.ConnectionString);
        await reset.OpenAsync();
        await using var resetTransaction = await reset.BeginTransactionAsync();
        await Execute(reset, $"SELECT 1 FROM platform.platform_user WHERE id = '{user.Id}' FOR UPDATE");
        await Execute(reset, $"UPDATE platform.platform_user SET totp_secret_version = totp_secret_version + 1 WHERE id = '{user.Id}'");

        // The sign-in side verified its code against the version it read BEFORE the reset.
        await using var db = Open();
        var signIn = new EFOperatorSessionStore(db).CreateSessionWithCodeAsync(
            user.Id, user.TotpSecretVersion, step: 5000, DateTimeOffset.UtcNow);

        await Task.Delay(BlockedFor);
        Assert.False(signIn.IsCompleted, "the sign-in should be waiting on the operator's row lock");

        await resetTransaction.CommitAsync();

        Assert.Null(await signIn);
        await using var check = Open();
        Assert.Empty(await check.PlatformSessions.Where(s => s.PlatformUserId == user.Id).ToListAsync());
    }

    [Fact]
    public async Task A_session_created_by_a_sign_in_in_flight_is_revoked_by_the_reset_that_waited_for_it()
    {
        var user = await NewOperatorAsync();

        // The sign-in side, held open: code used (row locked), session inserted, not yet committed.
        await using var signIn = new NpgsqlConnection(fixture.ConnectionString);
        await signIn.OpenAsync();
        await using var signInTransaction = await signIn.BeginTransactionAsync();
        await Execute(signIn, $"UPDATE platform.platform_user SET totp_last_used_step = 5000 WHERE id = '{user.Id}'");
        var sessionId = Guid.NewGuid();
        await Execute(signIn,
            "INSERT INTO platform.platform_session (id, platform_user_id, mfa_satisfied, created_at, last_seen_at, absolute_expiry) " +
            $"VALUES ('{sessionId}', '{user.Id}', true, now(), now(), now() + interval '8 hours')");

        await using var db = Open();
        var reset = ResetPlatformUserMfa.ResetAsync(db, await TestPlatformKeys.LoadAsync(db), user.Email, TimeProvider.System);

        await Task.Delay(BlockedFor);
        Assert.False(reset.IsCompleted, "the reset should be waiting on the operator's row lock");

        await signInTransaction.CommitAsync();

        Assert.Equal(0, (await reset).ExitCode);
        await using var check = Open();
        var session = await check.PlatformSessions.SingleAsync(s => s.Id == sessionId);
        Assert.NotNull(session.RevokedAt);
    }

    [Fact]
    public async Task A_process_starting_while_another_creates_the_first_key_waits_and_holds_that_key()
    {
        // Otherwise the API and an operator command starting at once can each create a first key,
        // and one encrypts a value the other cannot read until it restarts. Made to happen every
        // run: at the moment ours is about to insert the first key, a competitor starts.
        var schema = await CreateKeyTableAsync();
        var kek = NewKek();

        var (ours, competitor) = await StartWithCompetitorAsync(schema, kek, competitorKek: kek);

        var theirs = await competitor;
        await using (var db = new KeysContext(schema, fixture.ConnectionString))
            Assert.Single(await db.Set<DataKey>().ToListAsync());
        Assert.Equal("ours", theirs.Decrypt(ours.Encrypt("ours", "t.c:1"), "t.c:1"));
        Assert.Equal("theirs", ours.Decrypt(theirs.Encrypt("theirs", "t.c:1"), "t.c:1"));
    }

    [Fact]
    public async Task A_process_with_a_different_key_encryption_key_fails_at_startup_and_writes_nothing()
    {
        // The misconfigured racer must not add its own key alongside: two keys would then share
        // nothing, and each process would encrypt values the other cannot read.
        var schema = await CreateKeyTableAsync();
        var kek = NewKek();

        var (_, competitor) = await StartWithCompetitorAsync(schema, kek, competitorKek: NewKek());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => competitor);
        Assert.Contains("not wrapped by the configured key-encryption key", error.Message, StringComparison.Ordinal);
        await using var db = new KeysContext(schema, fixture.ConnectionString);
        Assert.Equal(kek.Fingerprint, Assert.Single(await db.Set<DataKey>().ToListAsync()).KekFingerprint);
    }

    /// <summary>
    /// Loads a key ring for <paramref name="schema"/>; just before its first save, starts a
    /// competing load and checks that the competitor is WAITING (on the key table's lock), not
    /// racing ahead. Returns our ring and the competitor, still running or finished.
    /// </summary>
    private async Task<(KeyRing Ours, Task<KeyRing> Competitor)> StartWithCompetitorAsync(
        string schema, KeyEncryptionKey kek, KeyEncryptionKey competitorKek)
    {
        Task<KeyRing>? competitor = null;
        var interruption = new BeforeFirstSave(async () =>
        {
            competitor = Task.Run(async () =>
            {
                await using var other = new KeysContext(schema, fixture.ConnectionString);
                return await KeyRing.LoadOrCreateAsync(other, competitorKek, TimeProvider.System);
            });
            await Task.Delay(BlockedFor);
            Assert.False(competitor.IsCompleted, "the competitor should be waiting on the key table's lock");
        });

        await using var db = new KeysContext(schema, fixture.ConnectionString, interruption);
        var ours = await KeyRing.LoadOrCreateAsync(db, kek, TimeProvider.System);

        return (ours, competitor ?? throw new InvalidOperationException("the first save never happened"));
    }

    private static KeyEncryptionKey NewKek()
        => KeyEncryptionKey.FromConfiguration(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), "Test:Kek");

    [Fact]
    public async Task Processes_starting_together_on_an_empty_key_table_all_hold_the_same_key()
    {
        // The same property under a real burst, as a backstop to the deterministic test above.
        var schema = await CreateKeyTableAsync();
        var kek = NewKek();

        var rings = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = new KeysContext(schema, fixture.ConnectionString);
            return await KeyRing.LoadOrCreateAsync(db, kek, TimeProvider.System);
        }));

        await using (var db = new KeysContext(schema, fixture.ConnectionString))
            Assert.Single(await db.Set<DataKey>().ToListAsync());

        foreach (var writer in rings)
        {
            var value = writer.Encrypt("shared", "t.c:1");
            Assert.All(rings, reader => Assert.Equal("shared", reader.Decrypt(value, "t.c:1")));
        }
    }

    /// <summary>A fresh schema holding an empty data_key table shaped as the migrations create it.</summary>
    private async Task<string> CreateKeyTableAsync()
    {
        var schema = $"keyrace_{Guid.NewGuid():N}";
        await using var setup = new NpgsqlConnection(fixture.ConnectionString);
        await setup.OpenAsync();
        await Execute(setup,
            $"CREATE SCHEMA {schema}; CREATE TABLE {schema}.data_key (id uuid NOT NULL, " +
            "kek_fingerprint character varying(16) NOT NULL, wrapped_key bytea NOT NULL, " +
            "created_at timestamp with time zone NOT NULL, PRIMARY KEY (id, kek_fingerprint))");
        return schema;
    }

    /// <summary>Runs something once, just before the context's first save reaches the database.</summary>
    private sealed class BeforeFirstSave(Func<Task> action) : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        private bool _done;

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!_done)
            {
                _done = true;
                await action();
            }
            return result;
        }
    }

    private sealed class KeysContext(
        string schema, string connectionString, Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor? interceptor = null) : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder options)
        {
            options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention()
                // One model per schema: EF caches the model by context type unless told otherwise.
                .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, SchemaModelCacheKey>();
            if (interceptor is not null) options.AddInterceptors(interceptor);
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddDataKeys(schema);

        public string Schema => schema;
    }

    private sealed class SchemaModelCacheKey : Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) => (context.GetType(), ((KeysContext)context).Schema, designTime);
    }

    private static async Task Execute(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
