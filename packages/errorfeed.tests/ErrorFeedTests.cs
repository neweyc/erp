using System.Net;
using System.Text.Json;
using AppPlatform.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AppPlatform.ErrorFeed.Tests;

public class ErrorFeedTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private static List<ErrorOccurrence> Drain(ErrorFeed feed)
    {
        var drained = new List<ErrorOccurrence>();
        while (feed.Reader.TryRead(out var occurrence)) drained.Add(occurrence);
        return drained;
    }

    private static Exception Thrown(string message)
    {
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (InvalidOperationException caught)
        {
            return caught;
        }
    }

    // ---- fingerprints -------------------------------------------------------------------------

    [Fact]
    public void The_same_fault_with_different_messages_has_one_fingerprint()
    {
        // The message is where customer data turns up ("no employee ada@example.com"), so it plays
        // no part: two occurrences of one fault group together whatever they say.
        Assert.Equal(
            ErrorFingerprint.ForException(Thrown("no employee ada@example.com")),
            ErrorFingerprint.ForException(Thrown("no employee grace@example.com")));
    }

    [Fact]
    public void A_different_exception_type_is_a_different_fault()
        => Assert.NotEqual(
            ErrorFingerprint.ForException(Thrown("x")),
            ErrorFingerprint.ForException(new ArgumentException("x")));

    [Fact]
    public void A_fingerprint_is_sixteen_hex_characters_as_the_platform_accepts()
        => Assert.Matches("^[0-9a-f]{16}$", ErrorFingerprint.ForException(Thrown("x")));

    // ---- the feed: bounded and rate-limited ---------------------------------------------------

    [Fact]
    public void A_fault_past_its_budget_is_counted_and_folded_into_the_next_one_sent()
    {
        var clock = new FakeTimeProvider(Now);
        var feed = new ErrorFeed("tickets", clock);

        for (var i = 0; i < ErrorFeed.PerFingerprintPerMinute + 5; i++)
            feed.Record(ErrorReference.New(), "0123456789abcdef", tenantId: 7);

        var firstMinute = Drain(feed);
        Assert.Equal(ErrorFeed.PerFingerprintPerMinute, firstMinute.Count);
        Assert.All(firstMinute, o => Assert.Equal(1, o.Count));

        clock.Advance(TimeSpan.FromMinutes(1));
        feed.Record(ErrorReference.New(), "0123456789abcdef", tenantId: 7);

        // The five dropped last minute are not lost from the count.
        Assert.Equal(6, Assert.Single(Drain(feed)).Count);
    }

    [Fact]
    public void Counts_held_back_for_one_tenant_are_never_credited_to_another()
    {
        var clock = new FakeTimeProvider(Now);
        var feed = new ErrorFeed("tickets", clock);
        for (var i = 0; i < ErrorFeed.PerFingerprintPerMinute + 5; i++)
            feed.Record(ErrorReference.New(), "0123456789abcdef", tenantId: 1);
        Drain(feed);

        clock.Advance(TimeSpan.FromMinutes(1));
        feed.Record(ErrorReference.New(), "0123456789abcdef", tenantId: 2);
        feed.Record(ErrorReference.New(), "0123456789abcdef", tenantId: 1);

        var next = Drain(feed);
        Assert.Equal(1, next.Single(o => o.TenantId == 2).Count);
        Assert.Equal(6, next.Single(o => o.TenantId == 1).Count);
    }

    [Fact]
    public void A_count_saturates_at_the_most_the_platform_accepts_rather_than_being_refused()
    {
        var clock = new FakeTimeProvider(Now);
        var feed = new ErrorFeed("tickets", clock);
        for (var i = 0; i < ErrorFeed.MaxCount + 50; i++)
            feed.Record(ErrorReference.New(), "0123456789abcdef", null);
        Drain(feed);

        clock.Advance(TimeSpan.FromMinutes(1));
        feed.Record(ErrorReference.New(), "0123456789abcdef", null);

        Assert.Equal(ErrorFeed.MaxCount, Assert.Single(Drain(feed)).Count);
    }

    [Fact]
    public void A_full_budget_table_drops_the_new_fault_and_never_resets_an_active_limit()
    {
        // Resetting every budget when the table filled would let a fault escape its limit simply
        // because many other faults happened at once.
        var clock = new FakeTimeProvider(Now);
        var feed = new ErrorFeed("tickets", clock);
        const string hot = "ffffffffffffffff";
        for (var i = 0; i < ErrorFeed.PerFingerprintPerMinute; i++) feed.Record(ErrorReference.New(), hot, null);
        for (var i = 1; i < ErrorFeed.MaxTrackedFingerprints; i++)
            feed.Record(ErrorReference.New(), i.ToString("x16", System.Globalization.CultureInfo.InvariantCulture), null);
        Drain(feed);

        feed.Record(ErrorReference.New(), "eeeeeeeeeeeeeeee", null); // a new fault, table full: dropped
        feed.Record(ErrorReference.New(), hot, null); // the hot fault: still over its limit

        Assert.Empty(Drain(feed));

        // A minute on, finished budgets make room again.
        clock.Advance(TimeSpan.FromMinutes(1));
        feed.Record(ErrorReference.New(), "eeeeeeeeeeeeeeee", null);
        Assert.Single(Drain(feed));
    }

    [Fact]
    public void Each_fault_has_its_own_budget()
    {
        var feed = new ErrorFeed("tickets", new FakeTimeProvider(Now));

        for (var i = 0; i < ErrorFeed.PerFingerprintPerMinute + 1; i++)
        {
            feed.Record(ErrorReference.New(), "aaaaaaaaaaaaaaaa", null);
            feed.Record(ErrorReference.New(), "bbbbbbbbbbbbbbbb", null);
        }

        Assert.Equal(2 * ErrorFeed.PerFingerprintPerMinute, Drain(feed).Count);
    }

    [Fact]
    public void A_full_queue_drops_rather_than_blocks_or_throws()
    {
        var feed = new ErrorFeed("tickets", new FakeTimeProvider(Now));

        // Distinct faults, so no rate limit applies: only the queue bound does.
        for (var i = 0; i < ErrorFeed.QueueCapacity + 500; i++)
            feed.Record(ErrorReference.New(), i.ToString("x16", System.Globalization.CultureInfo.InvariantCulture), null);

        Assert.Equal(ErrorFeed.QueueCapacity, Drain(feed).Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Tickets")]
    [InlineData("tickets; drop table")]
    [InlineData("a")]
    public void An_app_name_the_platform_would_refuse_is_refused_at_startup(string app)
        => Assert.Throws<ArgumentException>(() => new ErrorFeed(app, TimeProvider.System));

    // ---- the logger ---------------------------------------------------------------------------

    private static (ILogger Logger, ErrorFeed Feed) LoggerFor(string category)
    {
        var feed = new ErrorFeed("core", new FakeTimeProvider(Now));
        return (new ErrorFeedLoggerProvider(feed).CreateLogger(category), feed);
    }

    [Fact]
    public void An_error_log_is_forwarded_with_its_reference_and_tenant()
    {
        var (logger, feed) = LoggerFor("AppPlatform.Outbox.OutboxWorker");

        logger.LogError(Thrown("x"), "Failed {ErrorReference} for {TenantId}", "err_0000000000000000000000001", 42);

        var occurrence = Assert.Single(Drain(feed));
        Assert.Equal("err_0000000000000000000000001", occurrence.Reference);
        Assert.Equal(42, occurrence.TenantId);
        Assert.Equal("core", occurrence.App);
    }

    [Fact]
    public void Only_the_template_of_a_log_without_an_exception_is_used_never_its_values()
    {
        var (logger, feed) = LoggerFor("AppPlatform.Core.Something");

        logger.LogError("Could not deliver to {Address}", "ada@example.com");
        logger.LogError("Could not deliver to {Address}", "grace@example.com");

        var occurrences = Drain(feed);
        Assert.Equal(2, occurrences.Count);
        Assert.Single(occurrences.Select(o => o.Fingerprint).Distinct());
        Assert.Equal(
            ErrorFingerprint.ForLogEntry("AppPlatform.Core.Something", default, "Could not deliver to {Address}"),
            occurrences[0].Fingerprint);
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add($"{logLevel}: {formatter(state, exception)}");
    }

    [Fact]
    public void An_error_without_a_reference_gets_one_that_is_also_written_to_the_local_log()
    {
        // A background error (the outbox worker's, say) has no request and no reference. The one the
        // feed gives it must be findable in the service's own log, where the detail is.
        var feed = new ErrorFeed("core", new FakeTimeProvider(Now));
        var local = new ListLogger();
        var logger = new ErrorFeedLoggerProvider(feed, () => local).CreateLogger("AppPlatform.Outbox.OutboxWorker");

        logger.LogError(Thrown("x"), "Outbox sweep failed");

        var reference = Assert.Single(Drain(feed)).Reference;
        // Enough to find the error it belongs to: the same reference, and the error's category.
        Assert.Contains(local.Lines, line => line.StartsWith("Warning:", StringComparison.Ordinal)
            && line.Contains(reference, StringComparison.Ordinal)
            && line.Contains("AppPlatform.Outbox.OutboxWorker", StringComparison.Ordinal));
    }

    [Fact]
    public void An_error_that_already_has_a_reference_writes_no_extra_line()
    {
        var feed = new ErrorFeed("core", new FakeTimeProvider(Now));
        var local = new ListLogger();
        var logger = new ErrorFeedLoggerProvider(feed, () => local).CreateLogger("AppPlatform.Api.Something");

        logger.LogError(Thrown("x"), "Failed {ErrorReference}", ErrorReference.New());

        Assert.Empty(local.Lines);
    }

    [Fact]
    public void The_correlation_line_itself_is_never_forwarded()
    {
        var (logger, feed) = LoggerFor(ErrorFeedLoggerProvider.CorrelationCategory);

        logger.LogError("anything");

        Assert.Empty(Drain(feed));
    }

    [Fact]
    public void Below_error_is_not_forwarded()
    {
        var (logger, feed) = LoggerFor("AppPlatform.Core.Something");

        logger.LogWarning("slow");
        logger.LogInformation("fine");

        Assert.Empty(Drain(feed));
    }

    [Fact]
    public void The_feeds_own_writer_is_never_forwarded()
    {
        // Otherwise a failing feed would report its own failures into itself.
        var (logger, feed) = LoggerFor(typeof(ErrorFeedWriter).FullName!);

        logger.LogError(Thrown("feed down"), "could not send");

        Assert.Empty(Drain(feed));
    }

    // ---- the middleware -----------------------------------------------------------------------

    private sealed class FixedTenant(int? tenantId) : ITenantProvider
    {
        public int? TenantId => tenantId;
    }

    [Fact]
    public async Task An_unhandled_exception_becomes_a_500_with_a_reference_and_nothing_of_the_exception()
    {
        var feed = new ErrorFeed("tickets", new FakeTimeProvider(Now));

        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton(feed);
                    services.AddSingleton<ILoggerProvider>(new ErrorFeedLoggerProvider(feed));
                    services.AddScoped<ITenantProvider>(_ => new FixedTenant(9));
                })
                .Configure(app =>
                {
                    app.UseErrorFeed();
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapGet("/boom", (HttpContext _) =>
                        throw new InvalidOperationException("customer ada@example.com not found")));
                }))
            .StartAsync();

        using var response = await host.GetTestClient().GetAsync("/boom");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("ada@example.com", body, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", body, StringComparison.Ordinal);

        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal("internal_error", json.GetProperty("problemCode").GetString());
        var reference = json.GetProperty("reference").GetString();

        // The same reference in the feed, attributed to the request's tenant.
        var occurrence = Assert.Single(Drain(feed));
        Assert.Equal(reference, occurrence.Reference);
        Assert.Equal(9, occurrence.TenantId);
    }
}
