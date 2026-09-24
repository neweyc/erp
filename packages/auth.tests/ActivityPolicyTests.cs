namespace AppPlatform.Auth.Tests;

public class ActivityPolicyTests
{
    private static readonly DateTimeOffset Now = Build.Now;
    private const string Ordinary = "/api/tickets/v1/tickets";

    [Fact]
    public void Touching_is_throttled_to_five_minutes_when_no_idle_window_is_set()
    {
        Assert.False(ActivityPolicy.ShouldTouch(Ordinary, Now.AddMinutes(-4), Now, null));
        Assert.True(ActivityPolicy.ShouldTouch(Ordinary, Now.AddMinutes(-5), Now, null));
    }

    [Fact]
    public void Touching_tightens_to_one_minute_once_a_tenant_configures_an_idle_window()
    {
        // A stale last_seen_at makes the idle window fire EARLY. The 5-minute default is
        // only safe while there is no window to be early against.
        Assert.False(ActivityPolicy.ShouldTouch(Ordinary, Now.AddSeconds(-59), Now, 30));
        Assert.True(ActivityPolicy.ShouldTouch(Ordinary, Now.AddMinutes(-1), Now, 30));
    }

    [Fact]
    public void The_touch_interval_stays_well_inside_the_shortest_configurable_window()
    {
        // The shortest window a tenant may set is 5 minutes. If the interval were not
        // comfortably shorter, drift alone could sign someone out.
        Assert.True(ActivityPolicy.ActiveInterval < TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void A_background_poll_is_not_activity()
    {
        // The bell polls on a timer. An idle timeout keyed on request traffic would never
        // fire while a tab sat open — a control that silently does nothing.
        Assert.False(ActivityPolicy.ShouldTouch(
            "/api/core/v1/notifications/poll", Now.AddHours(-3), Now, 30));
    }

    [Fact]
    public void The_activity_ping_is_never_treated_as_a_background_poll()
    {
        // It is the bridge between the browser's clock and the server's. Excluding it
        // would let the two drift until a user typing through a long form has their save
        // rejected — losing exactly the work the idle warning exists to protect.
        Assert.False(ActivityPolicy.IsBackgroundPoll(ActivityPolicy.ActivityPingPath));
    }
}
