using AppPlatform.Outbox;

namespace AppPlatform.Outbox.Tests;

public class DispatcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private static OutboxMessage Message(int attempts = 0) => new()
    {
        Transport = OutboxTransports.Email,
        Destination = "a@b.c",
        Payload = "{}",
        Attempts = attempts,
        NextAttemptAt = Now,
        CreatedAt = Now,
        LockedUntil = Now.AddMinutes(2),
    };

    [Fact]
    public void Success_completes_the_message_and_clears_the_error()
    {
        var message = Message();
        message.LastError = "a previous failure";

        OutboxDispatcher.Apply(message, DeliveryResult.Success, Now);

        Assert.Equal(OutboxStatus.Succeeded, message.Status);
        Assert.Equal(Now, message.CompletedAt);
        Assert.Null(message.LastError);
        Assert.Null(message.LockedUntil);
        Assert.Equal(1, message.Attempts);
    }

    [Fact]
    public void A_transient_failure_schedules_a_retry_and_keeps_the_reason()
    {
        var message = Message();

        OutboxDispatcher.Apply(message, DeliveryResult.Transient("503 from provider"), Now);

        Assert.Equal(OutboxStatus.Pending, message.Status);
        Assert.Equal(Now.AddSeconds(10), message.NextAttemptAt);
        Assert.Equal("503 from provider", message.LastError);
        Assert.Null(message.CompletedAt);
    }

    [Fact]
    public void The_lease_is_always_released_so_a_crashed_send_does_not_strand_a_row()
    {
        var message = Message();

        OutboxDispatcher.Apply(message, DeliveryResult.Transient("timeout"), Now);

        Assert.Null(message.LockedUntil);
    }

    [Fact]
    public void Backoff_grows_then_holds_at_its_longest_step()
    {
        // Indexed by failures so far, counting the one just recorded: the first failure is 1.
        var delays = Enumerable.Range(1, OutboxBackoff.MaxAttempts - 1)
            .Select(OutboxBackoff.DelayAfter)
            .ToList();

        Assert.Equal(
            [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2),
             TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)],
            delays);
    }

    [Fact]
    public void Attempts_beyond_the_schedule_are_dead_lettered_rather_than_retried_forever()
        => Assert.Null(OutboxBackoff.DelayAfter(OutboxBackoff.MaxAttempts));

    [Fact]
    public void The_last_transient_failure_dead_letters_instead_of_scheduling()
    {
        var message = Message(attempts: OutboxBackoff.MaxAttempts - 1);

        OutboxDispatcher.Apply(message, DeliveryResult.Transient("still down"), Now);

        // Dead, not deleted: somebody has to look at it, and a silently dropped notification
        // is indistinguishable from one that was never queued.
        Assert.Equal(OutboxStatus.Dead, message.Status);
        Assert.Equal(Now, message.CompletedAt);
        Assert.Equal("still down", message.LastError);
    }

    [Fact]
    public void A_permanent_failure_dead_letters_on_the_first_attempt()
    {
        var message = Message();

        OutboxDispatcher.Apply(message, DeliveryResult.Fatal("malformed address"), Now);

        // Spending the whole schedule to rediscover that an address is invalid delays every
        // message queued behind it.
        Assert.Equal(OutboxStatus.Dead, message.Status);
        Assert.Equal(1, message.Attempts);
    }

    [Fact]
    public void A_huge_error_is_truncated_rather_than_stored_whole()
    {
        var message = Message();

        OutboxDispatcher.Apply(message, DeliveryResult.Transient(new string('x', 5000)), Now);

        // Providers answer failed sends with HTML error pages.
        Assert.Equal(1000, message.LastError!.Length);
    }

    [Fact]
    public void The_first_failure_gets_the_shortest_delay()
    {
        // The off-by-one this guards: counting attempts before the increment skips the
        // 10-second step entirely, and that is the retry which recovers a provider blip
        // before anybody notices.
        Assert.Equal(TimeSpan.FromSeconds(10), OutboxBackoff.DelayAfter(1));
    }

    [Fact]
    public void The_lease_outlasts_the_first_retry_delay()
    {
        // If the lease expired before the first retry was due, a second worker could claim a
        // message whose send is still in flight — turning at-least-once into always-twice.
        Assert.True(OutboxBackoff.LeaseDuration > OutboxBackoff.DelayAfter(1));
    }
}
