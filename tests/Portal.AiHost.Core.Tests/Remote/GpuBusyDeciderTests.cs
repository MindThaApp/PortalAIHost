using Portal.AiHost.Core.Remote;

namespace Portal.AiHost.Core.Tests.Remote;

public class GpuBusyDeciderTests
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private GpuBusyDecider Make(params int[] own) => new(new GpuBusyOptions(), pid => own.Contains(pid), () => _now);

    private static GpuProcessLoad[] Load(int pid, double pct) => [new GpuProcessLoad(pid, pct)];

    private bool Feed(GpuBusyDecider d, double pct, int seconds = 2)
    {
        _now += TimeSpan.FromSeconds(seconds);
        return d.Update(Load(5, pct));
    }

    [Fact]
    public void Busy_only_after_sustained_window()
    {
        var d = Make();
        var busy = d.Update(Load(5, 80));
        for (var i = 0; i < 3; i++)
        {
            busy = Feed(d, 80);
        }

        Assert.False(busy);
        Assert.True(Feed(d, 80));
    }

    [Fact]
    public void One_low_sample_does_not_restart_the_window()
    {
        var d = Make();
        d.Update(Load(5, 80));
        Feed(d, 80);
        Feed(d, 5);
        Feed(d, 80);
        Feed(d, 80);
        Assert.True(Feed(d, 80));
    }

    [Fact]
    public void Mostly_low_samples_are_not_busy()
    {
        var d = Make();
        d.Update(Load(5, 80));
        foreach (var pct in new[] { 5.0, 80, 5, 5, 80 })
        {
            Feed(d, pct);
        }

        Assert.False(d.Busy);
    }

    [Fact]
    public void Too_few_samples_are_not_busy()
    {
        var d = Make();
        d.Update(Load(5, 80));
        Assert.False(Feed(d, 80, 10));
    }

    [Fact]
    public void No_data_sample_leaves_history_alone()
    {
        var d = Make();
        d.Update(Load(5, 80));
        for (var i = 0; i < 3; i++)
        {
            Feed(d, 80);
        }

        _now += TimeSpan.FromSeconds(2);
        Assert.False(d.Update(GpuSamples.NoData));
        Assert.True(Feed(d, 80));
    }

    [Fact]
    public void Stays_busy_through_cool_down_then_clears()
    {
        var d = Make();
        d.Update(Load(5, 80));
        for (var i = 0; i < 5; i++)
        {
            Feed(d, 80);
        }

        Assert.True(d.Busy);
        _now += TimeSpan.FromSeconds(29);
        Assert.True(d.Update([]));
        _now += TimeSpan.FromSeconds(1);
        Assert.False(d.Update([]));
    }

    [Fact]
    public void Our_own_processes_are_ignored()
    {
        var d = Make(7);
        for (var i = 0; i < 8; i++)
        {
            _now += TimeSpan.FromSeconds(2);
            d.Update(Load(7, 99));
        }

        Assert.False(d.Busy);
    }

    [Fact]
    public void Unavailable_samples_count_as_quiet()
    {
        var d = Make();
        d.Update(null);
        _now += TimeSpan.FromSeconds(60);
        Assert.False(d.Update(null));
    }

    [Fact]
    public void Composite_is_busy_if_any_is()
    {
        Assert.True(new CompositeBusyDetector(new NeverBusyDetector(), new Always()).IsBusy());
        Assert.False(new CompositeBusyDetector(new NeverBusyDetector()).IsBusy());
    }

    private sealed class Always : IBusyDetector
    {
        public bool IsBusy() => true;
    }
}
