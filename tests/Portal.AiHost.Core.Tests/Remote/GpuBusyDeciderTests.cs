using Portal.AiHost.Core.Remote;

namespace Portal.AiHost.Core.Tests.Remote;

public class GpuBusyDeciderTests
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private GpuBusyDecider Make(params int[] own) => new(new GpuBusyOptions(), pid => own.Contains(pid), () => _now);

    private static GpuProcessLoad[] Load(int pid, double pct) => [new GpuProcessLoad(pid, pct)];

    [Fact]
    public void Busy_only_after_sustained_window()
    {
        var d = Make();
        Assert.False(d.Update(Load(5, 80)));
        _now += TimeSpan.FromSeconds(9);
        Assert.False(d.Update(Load(5, 80)));
        _now += TimeSpan.FromSeconds(1);
        Assert.True(d.Update(Load(5, 80)));
    }

    [Fact]
    public void A_dip_below_threshold_restarts_the_window()
    {
        var d = Make();
        d.Update(Load(5, 80));
        _now += TimeSpan.FromSeconds(8);
        d.Update(Load(5, 10));
        _now += TimeSpan.FromSeconds(2);
        Assert.False(d.Update(Load(5, 80)));
        _now += TimeSpan.FromSeconds(9);
        Assert.False(d.Update(Load(5, 80)));
    }

    [Fact]
    public void Stays_busy_through_cool_down_then_clears()
    {
        var d = Make();
        d.Update(Load(5, 80));
        _now += TimeSpan.FromSeconds(10);
        Assert.True(d.Update(Load(5, 80)));
        _now += TimeSpan.FromSeconds(29);
        Assert.True(d.Update([]));
        _now += TimeSpan.FromSeconds(1);
        Assert.False(d.Update([]));
    }

    [Fact]
    public void Our_own_processes_are_ignored()
    {
        var d = Make(7);
        d.Update(Load(7, 99));
        _now += TimeSpan.FromSeconds(20);
        Assert.False(d.Update(Load(7, 99)));
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
