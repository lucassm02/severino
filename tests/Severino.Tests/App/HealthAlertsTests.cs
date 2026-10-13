using Severino.App.Services;

namespace Severino.Tests.App;

public sealed class HealthAlertsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Api = Guid.NewGuid();
    private static readonly Guid Web = Guid.NewGuid();

    private static DateTimeOffset At(int seconds) => T0.AddSeconds(seconds);

    [Fact]
    public void Tells_a_fall_once_after_the_grace_and_the_return_after_it()
    {
        var alerts = new HealthAlerts(TimeSpan.FromSeconds(15));
        alerts.Observe(Api, up: true, At(0));
        alerts.Observe(Api, up: false, At(10));

        Assert.Empty(alerts.Take(At(20), _ => true).Down);
        Assert.Equal([Api], alerts.Take(At(25), _ => true).Down);
        Assert.Empty(alerts.Take(At(30), _ => true).Down);

        alerts.Observe(Api, up: true, At(40));
        Assert.Equal([Api], alerts.Take(At(41), _ => true).Back);
        Assert.Empty(alerts.Take(At(42), _ => true).Back);
    }

    [Fact]
    public void A_quick_restart_says_nothing()
    {
        var alerts = new HealthAlerts(TimeSpan.FromSeconds(15));
        alerts.Observe(Api, up: true, At(0));
        alerts.Observe(Api, up: false, At(10));
        alerts.Observe(Api, up: true, At(18));

        var (down, back) = alerts.Take(At(30), _ => true);
        Assert.Empty(down);
        Assert.Empty(back);
    }

    [Fact]
    public void Never_answered_is_not_news()
    {
        var alerts = new HealthAlerts(TimeSpan.FromSeconds(15));
        alerts.Observe(Api, up: false, At(0));

        Assert.Empty(alerts.Take(At(60), _ => true).Down);
    }

    [Fact]
    public void Only_what_asked_is_told_and_the_rest_is_dropped()
    {
        var alerts = new HealthAlerts(TimeSpan.FromSeconds(15));
        foreach (var id in new[] { Api, Web })
        {
            alerts.Observe(id, up: true, At(0));
            alerts.Observe(id, up: false, At(1));
        }

        Assert.Equal([Api], alerts.Take(At(20), id => id == Api).Down);
        // Web was dropped: asking for it later does not bring up the old fall.
        Assert.Empty(alerts.Take(At(25), _ => true).Down);
    }

    [Fact]
    public void Reset_forgets_falls_and_what_answered()
    {
        var alerts = new HealthAlerts(TimeSpan.FromSeconds(15));
        alerts.Observe(Api, up: true, At(0));
        alerts.Observe(Api, up: false, At(1));
        alerts.Reset();

        Assert.Empty(alerts.Take(At(30), _ => true).Down);
        alerts.Observe(Api, up: false, At(31));
        Assert.Empty(alerts.Take(At(60), _ => true).Down);
    }
}
