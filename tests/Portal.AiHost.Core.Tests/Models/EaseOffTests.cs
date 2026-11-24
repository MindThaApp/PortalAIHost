using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Planning;
using Portal.AiHost.Core.Runtime;

namespace Portal.AiHost.Core.Tests.Models;

public class EaseOffTests
{
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public void A_lease_lasts_until_its_time_to_live_and_renewing_extends_it()
    {
        var time = new ManualTime();
        var leases = new EaseOffLeases(time);
        Assert.False(leases.Any);

        Assert.True(leases.Set("app.a", "live_audio", 30_000, null));
        Assert.True(leases.Any);
        time.Advance(TimeSpan.FromSeconds(29));
        Assert.True(leases.Any);

        Assert.True(leases.Set("app.a", "live_audio", 30_000, null)); // renewed
        time.Advance(TimeSpan.FromSeconds(29));
        Assert.True(leases.Any);
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(leases.Any);
        Assert.Null(leases.NextExpiry);
    }

    [Fact]
    public void Clear_ends_only_the_callers_lease()
    {
        var leases = new EaseOffLeases(new ManualTime());
        leases.Set("app.a", "x", 10_000, 100);
        leases.Set("app.b", "y", 10_000, 200);
        Assert.Equal(300, leases.ReservedBytes);
        Assert.True(leases.Clear("app.a"));
        Assert.False(leases.Clear("app.a"));
        Assert.True(leases.Any);
        Assert.Equal(200, leases.ReservedBytes);
        Assert.True(leases.Clear("app.b"));
        Assert.False(leases.Any);
    }

    [Fact]
    public void Time_to_live_is_clamped_and_the_reason_is_cleaned()
    {
        Assert.Equal(1000, EaseOffLeases.ClampTtl(1));
        Assert.Equal(120_000, EaseOffLeases.ClampTtl(int.MaxValue));
        Assert.Equal(30_000, EaseOffLeases.ClampTtl(30_000));
        Assert.Equal("live_audio", EaseOffLeases.CleanReason(" live_audio "));
        Assert.Equal("busy", EaseOffLeases.CleanReason("   "));
        Assert.Equal("busy", EaseOffLeases.CleanReason(null));
        Assert.Equal("abc", EaseOffLeases.CleanReason("a<b>c\n"));
        Assert.Equal(EaseOffLeases.MaxReasonLength, EaseOffLeases.CleanReason(new string('a', 500)).Length);
    }

    [Fact]
    public void Too_many_apps_are_refused_but_a_renewal_always_works()
    {
        var leases = new EaseOffLeases(new ManualTime());
        for (var i = 0; i < EaseOffLeases.MaxLeases; i++)
        {
            Assert.True(leases.Set("app." + i, "x", 10_000, null));
        }

        Assert.False(leases.Set("one.more", "x", 10_000, null));
        Assert.True(leases.Set("app.0", "x", 10_000, null));
    }

    [Fact]
    public void Flags_keep_only_known_values_once_in_the_shown_order_and_never_saved_means_defaults()
    {
        Assert.Equal(["fullscreen", "battery", "app_busy"], EaseOffReasons.Normalize(null));
        Assert.Empty(EaseOffReasons.Normalize([]));
        Assert.Equal(["fullscreen", "gpu_busy", "battery", "app_busy"],
            EaseOffReasons.Normalize(["app_busy", "BATTERY", "gpu_busy", "bogus", "fullscreen", "app_busy"]));
        Assert.Equal(["battery"], EaseOffReasons.Normalize(["battery", " ", null!]));
    }

    [Fact]
    public void Resolve_clamps_the_ceiling_and_normalizes_the_flags()
    {
        var hw = new HardwareProfile("windows", "x64", 12, 64L << 30, [], [], "fp");
        var l = PerformanceModel.Resolve(new HostSettings { CpuCeilingPercent = 5, EaseOff = ["battery", "x"] }, hw);
        Assert.Equal(20, l.CpuCeilingPercent);
        Assert.Equal(["battery"], l.EaseOffFlags);
        Assert.Equal(100, PerformanceModel.Resolve(new HostSettings { CpuCeilingPercent = 500 }, hw).CpuCeilingPercent);
        Assert.Equal(["fullscreen", "battery", "app_busy"], PerformanceModel.Resolve(new HostSettings(), hw).EaseOffFlags);
    }

    [Theory]
    [InlineData(100, 100, null)]
    [InlineData(99, 99, 9900)]
    [InlineData(70, 70, 7000)]
    [InlineData(20, 20, 2000)]
    [InlineData(5, 20, 2000)]
    [InlineData(250, 100, null)]
    public void Cpu_rate_is_hundredths_of_a_percent_and_none_at_one_hundred(int percent, int clamped, int? rate)
    {
        Assert.Equal(clamped, PerformanceModel.ClampCeiling(percent));
        Assert.Equal(rate, PerformanceModel.CpuRateFor(percent));
    }

    [Theory]
    [InlineData(100, false, 100)]
    [InlineData(100, true, 50)]
    [InlineData(70, true, 50)]
    [InlineData(30, true, 30)]
    [InlineData(70, false, 70)]
    public void Easing_off_never_raises_the_ceiling(int ceiling, bool eased, int expected)
    {
        Assert.Equal(expected, PerformanceModel.EffectiveCeiling(ceiling, eased));
    }

    [Fact]
    public void Reason_order_is_app_busy_then_fullscreen_then_gpu_then_battery_and_only_for_flags_that_are_on()
    {
        string[] all = ["fullscreen", "gpu_busy", "battery", "app_busy"];
        Assert.Equal("app_busy", AiHostRuntime.ChooseReason(all, true, () => true, () => true, () => true));
        Assert.Equal("fullscreen", AiHostRuntime.ChooseReason(all, false, () => true, () => true, () => true));
        Assert.Equal("gpu_busy", AiHostRuntime.ChooseReason(all, false, () => false, () => true, () => true));
        Assert.Equal("battery", AiHostRuntime.ChooseReason(all, false, () => false, () => false, () => true));
        Assert.Null(AiHostRuntime.ChooseReason(all, false, () => false, () => false, () => false));
        Assert.Null(AiHostRuntime.ChooseReason([], true, () => true, () => true, () => true));
    }

    [Fact]
    public void A_flag_that_is_off_is_never_checked()
    {
        var calls = 0;
        AiHostRuntime.ChooseReason(["battery"], false, () => { calls++; return true; }, () => { calls++; return true; }, () => false);
        Assert.Equal(0, calls);
    }
}
