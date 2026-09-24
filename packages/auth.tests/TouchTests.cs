using System.Net;

namespace AppPlatform.Auth.Tests;

public class TouchTests
{
    [Fact]
    public async Task A_recent_session_is_not_touched_again()
    {
        var store = new StubSessionStore(Build.Session(lastSeenAt: Build.Now.AddSeconds(-30)));

        await Pipeline.SendAsync(null, sessionId: Pipeline.SessionId, store: store);

        // Unthrottled, every read becomes a write transaction.
        Assert.Equal(0, store.Touches);
    }

    [Fact]
    public async Task A_stale_session_is_touched()
    {
        var store = new StubSessionStore(Build.Session(lastSeenAt: Build.Now.AddMinutes(-10)));

        await Pipeline.SendAsync(null, sessionId: Pipeline.SessionId, store: store);

        Assert.Equal(1, store.Touches);
    }

    [Fact]
    public async Task A_background_poll_never_counts_as_activity()
    {
        var store = new StubSessionStore(
            Build.Session(lastSeenAt: Build.Now.AddHours(-2), idleTimeoutMinutes: 30));

        await Pipeline.SendAsync(null, path: "/api/core/v1/notifications/poll",
            sessionId: Pipeline.SessionId, store: store);

        // The bell polls on a timer. Counting it would mean an idle timeout that never
        // fires while a tab sits open — a control that silently does nothing.
        Assert.Equal(0, store.Touches);
    }

    [Fact]
    public async Task The_activity_ping_touches_even_inside_the_throttle_window()
    {
        var store = new StubSessionStore(
            Build.Session(lastSeenAt: Build.Now.AddSeconds(-5), idleTimeoutMinutes: 30));

        await Pipeline.SendAsync(null, path: ActivityPolicy.ActivityPingPath,
            sessionId: Pipeline.SessionId, store: store);

        // The bridge between the browser's clock and the server's. Subject to the throttle,
        // the two drift until someone typing through a long form has their save rejected —
        // losing exactly the work the idle warning exists to protect.
        Assert.Equal(1, store.Touches);
    }

    [Fact]
    public async Task A_failing_touch_does_not_fail_the_request()
    {
        var store = new StubSessionStore(Build.Session(lastSeenAt: Build.Now.AddMinutes(-10)))
        {
            ThrowOnTouch = true,
        };

        var probe = await Pipeline.SendAsync(null, sessionId: Pipeline.SessionId, store: store);

        // A missed touch costs a session that times out slightly early. Failing the request
        // costs the user their work, because bookkeeping did not commit.
        Assert.Equal(HttpStatusCode.OK, probe.Status);
    }
}
