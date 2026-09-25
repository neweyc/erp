using AppPlatform.Core.Data;
using AppPlatform.Outbox;
using AppPlatform.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace AppPlatform.PrivilegeTests;

/// <summary>
/// The outbox worker against real PostgreSQL.
///
/// Real database because two of the things most likely to break are invisible without one: the
/// claim's row locking, and the fact that outbox rows are tenant-scoped, so saving a delivery
/// result without entering the row's tenant is refused by TenantGuard.
/// </summary>
public class OutboxWorkerTests : IAsyncLifetime
{
    private Testcontainers.PostgreSql.PostgreSqlContainer _container = null!;
    private string _connection = "";
    private string _captureDirectory = "";

    public async Task InitializeAsync()
    {
        _container = new Testcontainers.PostgreSql.PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("appplatform").Build();
        await _container.StartAsync();
        _connection = _container.GetConnectionString();

        _captureDirectory = Directory.CreateTempSubdirectory("outbox-capture").FullName;

        foreach (var script in new[]
        {
            "database/privileges/01-roles-and-schemas.sql",
            "database/platform/migrations-all.sql",
            "database/core/migrations-all.sql",
        })
        {
            await RunFileAsync(Path.Combine(Boundary.RepositoryPaths.Root, script));
        }
    }

    public async Task DisposeAsync()
    {
        Directory.Delete(_captureDirectory, recursive: true);
        await _container.DisposeAsync();
    }

    private async Task RunFileAsync(string path)
    {
        var sql = string.Join('\n', (await File.ReadAllLinesAsync(path))
            .Where(l => !l.TrimStart().StartsWith('\\')));

        await using var connection = new NpgsqlConnection(_connection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>A transport whose outcome the test controls, standing in for the final hop only.</summary>
    private sealed class StubTransport(Func<OutboxMessage, DeliveryResult> respond) : IOutboxTransport
    {
        public List<Guid> Attempts { get; } = [];

        public string Transport => OutboxTransports.Email;

        public Task<DeliveryResult> SendAsync(OutboxMessage message, CancellationToken ct)
        {
            Attempts.Add(message.Id);
            return Task.FromResult(respond(message));
        }
    }

    /// <summary>
    /// A host with a scoped DbContext and tenant provider, which is what the worker needs: it
    /// creates one scope per message so it can enter that message's tenant before saving.
    /// </summary>
    private (OutboxWorker<CoreDbContext> Worker, ServiceProvider Provider) BuildWorker(
        IOutboxTransport? transport, FakeTimeProvider clock)
    {
        var services = new ServiceCollection();

        services.AddScoped<AmbientTenantProvider>();
        services.AddScoped<ITenantProvider>(sp => sp.GetRequiredService<AmbientTenantProvider>());
        services.AddScoped<IBackgroundTenantScope>(sp => sp.GetRequiredService<AmbientTenantProvider>());

        services.AddDbContext<CoreDbContext>(options => options
            .UseNpgsql(_connection).UseSnakeCaseNamingConvention());

        if (transport is not null) services.AddSingleton(transport);

        var provider = services.BuildServiceProvider();

        var worker = new OutboxWorker<CoreDbContext>(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new OutboxWorkerOptions { Schema = CoreDbContext.Schema, PollInterval = TimeSpan.FromMilliseconds(1) },
            clock,
            NullLogger<OutboxWorker<CoreDbContext>>.Instance);

        return (worker, provider);
    }

    private async Task<int> SeedTenantAsync(string name)
    {
        await using var connection = new NpgsqlConnection(_connection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO platform.tenant (public_id, name, status, created_at) " +
            "VALUES ($1, $2, 'Active', now()) RETURNING id", connection);
        command.Parameters.AddWithValue(Ids.PublicId.New("ten").ToString());
        command.Parameters.AddWithValue(name);

        return (int)(await command.ExecuteScalarAsync())!;
    }

    private async Task<Guid> StageAsync(int tenantId, DateTimeOffset now, string payload = """{"kind":"invite"}""")
    {
        var tenant = new AmbientTenantProvider();
        tenant.UseTenant(tenantId);

        await using var db = new CoreDbContext(
            new DbContextOptionsBuilder<CoreDbContext>()
                .UseNpgsql(_connection).UseSnakeCaseNamingConvention().Options,
            tenant);

        var message = new Outbox.Outbox(db, new FakeTimeProvider(now))
            .AddMessage(OutboxTransports.Email, "invitee@example.test", payload);

        await db.SaveChangesAsync();
        return message.Id;
    }

    private async Task<OutboxMessage> ReadAsync(int tenantId, Guid messageId)
    {
        var tenant = new AmbientTenantProvider();
        tenant.UseTenant(tenantId);

        await using var db = new CoreDbContext(
            new DbContextOptionsBuilder<CoreDbContext>()
                .UseNpgsql(_connection).UseSnakeCaseNamingConvention().Options,
            tenant);

        return await db.Set<OutboxMessage>().SingleAsync(m => m.Id == messageId);
    }

    [Fact]
    public async Task A_staged_message_is_delivered_and_marked_succeeded()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new FakeTimeProvider(now);
        var tenantId = await SeedTenantAsync("Deliver Ltd");
        var messageId = await StageAsync(tenantId, now);

        var transport = new StubTransport(_ => DeliveryResult.Success);
        var (worker, provider) = BuildWorker(transport, clock);
        await using var _ = provider;

        Assert.Equal(1, await worker.DrainAsync(CancellationToken.None));

        var message = await ReadAsync(tenantId, messageId);
        Assert.Equal(OutboxStatus.Succeeded, message.Status);
        Assert.Equal(1, message.Attempts);
        Assert.NotNull(message.CompletedAt);
        // The lease is released on the way out, so a crashed worker never strands a row.
        Assert.Null(message.LockedUntil);
    }

    [Fact]
    public async Task Delivery_works_for_several_tenants_in_one_sweep()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new FakeTimeProvider(now);
        var first = await SeedTenantAsync("Alpha Ltd");
        var second = await SeedTenantAsync("Beta Ltd");
        var a = await StageAsync(first, now);
        var b = await StageAsync(second, now);

        var (worker, provider) = BuildWorker(new StubTransport(_ => DeliveryResult.Success), clock);
        await using var _ = provider;

        // The real risk this covers: the claim is tenant-blind, but the SAVE is not. A worker
        // that did not enter each message's tenant would be refused by TenantGuard on the first
        // row and deliver nothing at all.
        Assert.Equal(2, await worker.DrainAsync(CancellationToken.None));

        Assert.Equal(OutboxStatus.Succeeded, (await ReadAsync(first, a)).Status);
        Assert.Equal(OutboxStatus.Succeeded, (await ReadAsync(second, b)).Status);
    }

    [Fact]
    public async Task A_transient_failure_stays_pending_and_is_rescheduled()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new FakeTimeProvider(now);
        var tenantId = await SeedTenantAsync("Retry Ltd");
        var messageId = await StageAsync(tenantId, now);

        var (worker, provider) = BuildWorker(
            new StubTransport(_ => DeliveryResult.Transient("503 from provider")), clock);
        await using var _ = provider;

        Assert.Equal(0, await worker.DrainAsync(CancellationToken.None));

        var message = await ReadAsync(tenantId, messageId);
        Assert.Equal(OutboxStatus.Pending, message.Status);
        Assert.Equal(1, message.Attempts);
        Assert.Equal("503 from provider", message.LastError);
        Assert.True(message.NextAttemptAt > now, "a failed message must not be retried immediately");
    }

    [Fact]
    public async Task A_message_that_is_not_yet_due_is_left_alone()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new FakeTimeProvider(now);
        var tenantId = await SeedTenantAsync("Backoff Ltd");
        await StageAsync(tenantId, now);

        var transport = new StubTransport(_ => DeliveryResult.Transient("down"));
        var (worker, provider) = BuildWorker(transport, clock);
        await using var _ = provider;

        await worker.DrainAsync(CancellationToken.None);
        // Same instant, so the backoff has not elapsed. Without it the worker would spin on a
        // failing message as fast as it could poll.
        await worker.DrainAsync(CancellationToken.None);

        Assert.Single(transport.Attempts);
    }

    [Fact]
    public async Task Repeated_failures_dead_letter_rather_than_retrying_forever()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new FakeTimeProvider(now);
        var tenantId = await SeedTenantAsync("Dead Ltd");
        var messageId = await StageAsync(tenantId, now);

        var (worker, provider) = BuildWorker(new StubTransport(_ => DeliveryResult.Transient("down")), clock);
        await using var _ = provider;

        for (var attempt = 0; attempt < OutboxBackoff.MaxAttempts; attempt++)
        {
            await worker.DrainAsync(CancellationToken.None);
            // Advance past the backoff so the next sweep is allowed to pick it up.
            clock.Advance(TimeSpan.FromHours(1));
        }

        var message = await ReadAsync(tenantId, messageId);
        // Dead, not deleted: someone still has to look at an invitation that never arrived.
        Assert.Equal(OutboxStatus.Dead, message.Status);
        Assert.Equal(OutboxBackoff.MaxAttempts, message.Attempts);
        Assert.Equal("down", message.LastError);
    }

    [Fact]
    public async Task A_transport_that_throws_is_treated_as_transient_and_does_not_stop_the_sweep()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new FakeTimeProvider(now);
        var tenantId = await SeedTenantAsync("Throwing Ltd");
        var thrower = await StageAsync(tenantId, now);
        var healthy = await StageAsync(tenantId, now, """{"kind":"second"}""");

        var (worker, provider) = BuildWorker(
            new StubTransport(m => m.Id == thrower
                ? throw new InvalidOperationException("provider exploded")
                : DeliveryResult.Success),
            clock);
        await using var _ = provider;

        Assert.Equal(1, await worker.DrainAsync(CancellationToken.None));

        // Transient, not fatal: a bug in a transport must not permanently discard a customer's
        // invitation on its first attempt.
        var failed = await ReadAsync(tenantId, thrower);
        Assert.Equal(OutboxStatus.Pending, failed.Status);
        Assert.Contains("provider exploded", failed.LastError!, StringComparison.Ordinal);

        // And the sweep continued past it rather than abandoning the batch.
        Assert.Equal(OutboxStatus.Succeeded, (await ReadAsync(tenantId, healthy)).Status);
    }

    [Fact]
    public async Task A_transport_timeout_is_recorded_and_does_not_abandon_the_batch()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new FakeTimeProvider(now);
        var tenantId = await SeedTenantAsync("Timeout Ltd");
        var timingOut = await StageAsync(tenantId, now);
        var healthy = await StageAsync(tenantId, now, """{"kind":"second"}""");

        // HttpClient throws TaskCanceledException — which derives from OperationCanceledException
        // — when its own Timeout elapses. That is the most common failure of any real transport.
        // Treated as shutdown, it skipped the dispatcher entirely: no attempt recorded, lease
        // never released, never dead-lettered, and the rest of the batch abandoned.
        var (worker, provider) = BuildWorker(
            new StubTransport(m => m.Id == timingOut
                ? throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout")
                : DeliveryResult.Success),
            clock);
        await using var _ = provider;

        Assert.Equal(1, await worker.DrainAsync(CancellationToken.None));

        var failed = await ReadAsync(tenantId, timingOut);
        Assert.Equal(OutboxStatus.Pending, failed.Status);
        Assert.Equal(1, failed.Attempts);
        Assert.NotNull(failed.LastError);
        Assert.True(failed.NextAttemptAt > now, "a timed-out send must be rescheduled by the backoff");
        // Released, or the retry cadence silently becomes the lease duration instead of the backoff.
        Assert.Null(failed.LockedUntil);

        // And the message behind it still went out: one slow destination must not block every
        // other tenant's mail on every sweep.
        Assert.Equal(OutboxStatus.Succeeded, (await ReadAsync(tenantId, healthy)).Status);
    }

    [Fact]
    public async Task A_failure_after_the_transport_does_not_abandon_the_batch()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new FakeTimeProvider(now);
        var tenantId = await SeedTenantAsync("Poison Ltd");
        var poison = await StageAsync(tenantId, now);
        var healthy = await StageAsync(tenantId, now, """{"kind":"second"}""");

        // The failure must escape DeliverAsync entirely, which a throwing transport cannot do —
        // SendAsync catches its own exceptions. So the transport reports success and corrupts the
        // tracked entity, making SaveChangesAsync fail with a value-too-long error. That is the
        // only path on which the per-message guard in DrainAsync is the thing standing between
        // one bad row and the rest of the batch.
        var (worker, provider) = BuildWorker(new StubTransport(m =>
        {
            if (m.Id == poison) m.Destination = new string('x', 10_000);
            return DeliveryResult.Success;
        }), clock);
        await using var _ = provider;

        await worker.DrainAsync(CancellationToken.None);

        // The poison row was not saved, so it stays claimable once its lease expires.
        Assert.Equal(OutboxStatus.Pending, (await ReadAsync(tenantId, poison)).Status);

        // And the message behind it still went out. Without the guard this assertion fails,
        // because the exception abandons the remaining batch.
        Assert.Equal(OutboxStatus.Succeeded, (await ReadAsync(tenantId, healthy)).Status);
    }

    [Fact]
    public async Task The_worker_survives_a_failing_sweep_and_keeps_running()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);

        // A scope factory that always throws stands in for a database that is briefly gone.
        var services = new ServiceCollection();
        var provider = services.BuildServiceProvider();

        var worker = new OutboxWorker<CoreDbContext>(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new OutboxWorkerOptions { Schema = "core", PollInterval = TimeSpan.FromMilliseconds(10) },
            clock,
            NullLogger<OutboxWorker<CoreDbContext>>.Instance);

        using var stopping = new CancellationTokenSource();
        await worker.StartAsync(stopping.Token);

        // An unhandled exception in a BackgroundService stops the HOST by default, so a
        // transient database blip would take the service down and stop delivering anything.
        await Task.Delay(100, CancellationToken.None);
        Assert.Null(worker.ExecuteTask?.Exception);
        Assert.NotEqual(TaskStatus.Faulted, worker.ExecuteTask?.Status);

        await worker.StopAsync(stopping.Token);
    }

    [Fact]
    public async Task A_message_with_no_registered_transport_is_dead_lettered_immediately()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new FakeTimeProvider(now);
        var tenantId = await SeedTenantAsync("Unrouted Ltd");
        var messageId = await StageAsync(tenantId, now);

        var (worker, provider) = BuildWorker(transport: null, clock);
        await using var _ = provider;

        await worker.DrainAsync(CancellationToken.None);

        // No amount of waiting conjures a transport, and retrying would hide the
        // misconfiguration behind a message that looks merely slow.
        var message = await ReadAsync(tenantId, messageId);
        Assert.Equal(OutboxStatus.Dead, message.Status);
        Assert.Contains("No transport registered", message.LastError!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delivered_messages_are_pruned_once_past_their_retention_window()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new FakeTimeProvider(now);
        var tenantId = await SeedTenantAsync("Prune Ltd");
        var messageId = await StageAsync(tenantId, now, """{"kind":"invite","token":"secret"}""");

        var (worker, provider) = BuildWorker(new StubTransport(_ => DeliveryResult.Success), clock);
        await using var _ = provider;

        await worker.DrainAsync(CancellationToken.None);
        Assert.Equal(OutboxStatus.Succeeded, (await ReadAsync(tenantId, messageId)).Status);

        // An invitation payload holds a plaintext, password-equivalent token. Retention is what
        // bounds how long that sits in a readable table and in every backup.
        clock.Advance(OutboxRetention.SucceededMessages + TimeSpan.FromDays(1));
        await worker.DrainAsync(CancellationToken.None);

        var tenant = new AmbientTenantProvider();
        tenant.UseTenant(tenantId);
        await using var db = new CoreDbContext(
            new DbContextOptionsBuilder<CoreDbContext>()
                .UseNpgsql(_connection).UseSnakeCaseNamingConvention().Options,
            tenant);

        Assert.Empty(await db.Set<OutboxMessage>().Where(m => m.Id == messageId).ToListAsync());
    }

    [Fact]
    public async Task A_recently_delivered_message_is_not_pruned()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new FakeTimeProvider(now);
        var tenantId = await SeedTenantAsync("Keep Ltd");
        var messageId = await StageAsync(tenantId, now);

        var (worker, provider) = BuildWorker(new StubTransport(_ => DeliveryResult.Success), clock);
        await using var _ = provider;

        await worker.DrainAsync(CancellationToken.None);
        // Past the prune throttle but well inside retention: a prune that deleted on every
        // sweep would destroy the delivery record the moment it was written.
        clock.Advance(TimeSpan.FromHours(2));
        await worker.DrainAsync(CancellationToken.None);

        Assert.Equal(OutboxStatus.Succeeded, (await ReadAsync(tenantId, messageId)).Status);
    }

    [Theory]
    [InlineData("tickets; DROP TABLE users")]
    [InlineData("Tickets")]
    [InlineData("")]
    public void An_invalid_schema_identifier_is_refused_at_construction(string schema)
    {
        // The schema is interpolated into the prune statement because an identifier cannot be a
        // parameter, so it is validated rather than trusted.
        Assert.Throws<ArgumentException>(() =>
            new OutboxWorkerOptions { Schema = schema });
    }

    [Fact]
    public async Task The_file_transport_captures_the_payload_verbatim()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new FakeTimeProvider(now);
        var tenantId = await SeedTenantAsync("Capture Ltd");
        var messageId = await StageAsync(tenantId, now, """{"kind":"invite","token":"abc123"}""");

        var (worker, provider) = BuildWorker(new FileEmailTransport(_captureDirectory), clock);
        await using var _ = provider;

        await worker.DrainAsync(CancellationToken.None);

        // The captured file is what the browser journey reads instead of the database, so the
        // token must survive the hop intact.
        var captured = await File.ReadAllTextAsync(Path.Combine(_captureDirectory, $"{messageId}.json"));
        Assert.Contains("abc123", captured, StringComparison.Ordinal);
        Assert.Contains("invitee@example.test", captured, StringComparison.Ordinal);
    }
}
