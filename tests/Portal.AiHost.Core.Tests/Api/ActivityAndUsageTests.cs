using Portal.AiHost.Core.Tests.Support;
using Portal.AiHost.Core.Runtime;

namespace Portal.AiHost.Core.Tests.Api;

public sealed class ActivityAndUsageTests
{
    private sealed class FakeProbe : IProcessUsageProbe
    {
        public TimeSpan Cpu;
        public int Reads;

        public ProcessUsage? Read(int processId)
        {
            Reads++;
            return processId == 99 ? null : new ProcessUsage(1000 + processId, 500, Cpu);
        }
    }

    [Fact]
    public void Counts_requests_tokens_and_running_per_app()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero));
        var t = new ActivityTracker(time, TimeZoneInfo.Utc);
        using (var a = t.Begin("app.a"))
        {
            Assert.Equal(0, t.Snapshot()[0].Running);
            a.StartGeneration();
            Assert.Equal(1, t.Snapshot()[0].Running);
            time.Advance(TimeSpan.FromSeconds(2));
            a.AddTokens(40);
        }

        var snap = t.Snapshot(id => id == "app.a" ? "App A" : null);
        Assert.Single(snap);
        Assert.Equal("App A", snap[0].AppName);
        Assert.Equal(1, snap[0].Requests);
        Assert.Equal(40, snap[0].Tokens);
        Assert.Equal(0, snap[0].Running);
        Assert.Equal(20.0, t.LastTokensPerSecond);
    }

    [Fact]
    public void Queued_request_counts_but_does_not_run_and_reset_happens_at_local_midnight()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 23, 23, 59, 0, TimeSpan.Zero));
        var t = new ActivityTracker(time, TimeZoneInfo.Utc);
        using (t.Begin("a")) { }
        t.Begin("a").Dispose();
        Assert.Equal(2, t.Snapshot()[0].Requests);
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Empty(t.Snapshot());
    }

    [Fact]
    public void Sampler_reads_at_most_every_interval_and_adds_cpu_and_sums_processes()
    {
        var time = new ManualTimeProvider();
        var probe = new FakeProbe();
        var s = new UsageSampler(() => [1, 2, 99], probe, time, cores: 4);
        var first = s.Get(null);
        Assert.Equal(2003, first.RamUsedBytes);
        Assert.Equal(1000, first.VramUsedBytes);
        Assert.Equal(0, first.CpuPercent);
        var reads = probe.Reads;
        time.Advance(TimeSpan.FromMilliseconds(500));
        s.Get(12.5);
        Assert.Equal(reads, probe.Reads); // cached
        time.Advance(TimeSpan.FromSeconds(1));
        probe.Cpu = TimeSpan.FromSeconds(1.5);
        var next = s.Get(12.5);
        Assert.True(probe.Reads > reads);
        Assert.Equal(12.5, next.GenerationTokensPerSecond);
        Assert.Equal(50, next.CpuPercent); // 3 s of cpu over 1.5 s on 4 cores
    }
}
