using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Runtime;

namespace Portal.AiHost.Core.Tests.Runtime;

public sealed class HealthCheckerTests
{
    private static readonly HealthModelInfo Ready = new("m1", "Qwen", 0, null, EngineState.Ready);
    private static readonly EngineFileStatus[] BothEngines = [new("cpu", true), new("vulkan", true)];

    private static HealthInputs Good() => new()
    {
        EngineFiles = BothEngines, GpuAvailable = true, GpuName = "Test GPU", ComputeEffective = "gpu",
        Model = Ready, StoreFreeBytes = 50L << 30, Fit = PerformanceFits.FitsFully, FitModel = "Qwen",
    };

    [Fact]
    public void A_healthy_host_has_all_checks_ok_and_no_fixes()
    {
        var r = HealthChecker.Evaluate(Good());
        Assert.Equal(HealthStatuses.Ok, r.Status);
        Assert.Equal(0, r.NeedsAttention);
        Assert.Equal(8, r.Checks.Count);
        Assert.All(r.Checks, c => Assert.Equal(HealthStatuses.Ok, c.Status));
        Assert.Equal(r.Checks.Count, r.Checks.Select(c => c.Id).Distinct().Count());
    }

    [Fact]
    public void Service_fails_when_the_api_is_not_listening()
    {
        Assert.Equal(HealthStatuses.Fail, HealthChecker.Service(Good() with { ApiListening = false }).Status);
        Assert.Equal(HealthStatuses.Ok, HealthChecker.Service(Good()).Status);
    }

    [Fact]
    public void Engine_fails_without_cpu_build_warns_without_vulkan_and_is_ok_when_unknown()
    {
        Assert.Equal(HealthStatuses.Fail,
            HealthChecker.Engine(Good() with { EngineFiles = [new("cpu", false), new("vulkan", true)] }).Status);
        Assert.Equal(HealthStatuses.Warn,
            HealthChecker.Engine(Good() with { EngineFiles = [new("cpu", true), new("vulkan", false)] }).Status);
        Assert.Equal(HealthStatuses.Ok, HealthChecker.Engine(Good() with { EngineFiles = null }).Status);
        Assert.Equal(HealthStatuses.Ok, HealthChecker.Engine(Good()).Status);
    }

    [Fact]
    public void Graphics_warns_not_fails_on_processor_only()
    {
        var none = HealthChecker.Graphics(Good() with { GpuAvailable = false, ComputeEffective = "cpu" });
        Assert.Equal(HealthStatuses.Warn, none.Status);
        Assert.Equal(HealthStatuses.Warn, HealthChecker.Graphics(Good() with { ComputeEffective = "cpu" }).Status);
        Assert.Equal(HealthStatuses.Ok, HealthChecker.Graphics(Good() with { Compute = "cpu", ComputeEffective = "cpu" }).Status);
        Assert.Equal(HealthStatuses.Ok, HealthChecker.Graphics(Good()).Status);
    }

    [Fact]
    public void A_damaged_model_fails_with_the_check_files_fix()
    {
        var c = HealthChecker.Model(Good() with { Model = Ready with { DamagedReason = "File does not match." } });
        Assert.Equal(HealthStatuses.Fail, c.Status);
        Assert.Equal(HealthFixes.CheckFiles, c.FixAction);
        Assert.Equal("m1", c.FixTarget);
        Assert.Contains("File does not match.", c.Detail);
    }

    [Fact]
    public void Missing_model_files_fail_with_the_check_files_fix()
    {
        var c = HealthChecker.Model(Good() with { Model = Ready with { MissingFiles = 1 } });
        Assert.Equal(HealthStatuses.Fail, c.Status);
        Assert.Equal(HealthFixes.CheckFiles, c.FixAction);
    }

    [Fact]
    public void Model_states_map_to_status_and_fix()
    {
        var failed = HealthChecker.Model(Good() with { Model = Ready with { EngineState = EngineState.Failed } });
        Assert.Equal((HealthStatuses.Fail, HealthFixes.RestartEngine), (failed.Status, failed.FixAction));

        var stopped = HealthChecker.Model(Good() with { Model = Ready with { EngineState = EngineState.Stopped } });
        Assert.Equal((HealthStatuses.Ok, HealthFixes.LoadModel), (stopped.Status, stopped.FixAction));

        var loading = HealthChecker.Model(Good() with { Model = Ready with { EngineState = EngineState.Loading } });
        Assert.Equal(HealthStatuses.Ok, loading.Status);
        Assert.Null(loading.FixAction);

        Assert.Null(HealthChecker.Model(Good()).FixAction);

        var none = HealthChecker.Model(Good() with { Model = null });
        Assert.Equal((HealthStatuses.Warn, HealthFixes.OpenModels), (none.Status, none.FixAction));
    }

    [Theory]
    [InlineData(50L << 30, HealthStatuses.Ok)]
    [InlineData(HealthChecker.DiskWarnBytes, HealthStatuses.Ok)]
    [InlineData(HealthChecker.DiskWarnBytes - 1, HealthStatuses.Warn)]
    [InlineData(HealthChecker.DiskFailBytes, HealthStatuses.Warn)]
    [InlineData(HealthChecker.DiskFailBytes - 1, HealthStatuses.Fail)]
    public void Disk_thresholds(long free, string expected) =>
        Assert.Equal(expected, HealthChecker.Disk(Good() with { StoreFreeBytes = free }).Status);

    [Fact]
    public void Disk_is_ok_when_unreadable() =>
        Assert.Equal(HealthStatuses.Ok, HealthChecker.Disk(Good() with { StoreFreeBytes = null }).Status);

    [Theory]
    [InlineData(PerformanceFits.FitsFully, HealthStatuses.Ok)]
    [InlineData(PerformanceFits.FitsPartial, HealthStatuses.Ok)]
    [InlineData(PerformanceFits.CpuOnly, HealthStatuses.Ok)]
    [InlineData(PerformanceFits.NeedsSmallerModel, HealthStatuses.Fail)]
    [InlineData(PerformanceFits.TooBig, HealthStatuses.Fail)]
    [InlineData(null, HealthStatuses.Ok)]
    public void Memory_follows_the_fit(string? fit, string expected)
    {
        var c = HealthChecker.Memory(Good() with { Fit = fit });
        Assert.Equal(expected, c.Status);
        Assert.Equal(expected == HealthStatuses.Fail ? HealthFixes.OpenPerformance : null, c.FixAction);
    }

    [Fact]
    public void Ease_off_is_reported_as_ok_with_the_reason_in_plain_words()
    {
        Assert.Equal(HealthStatuses.Ok, HealthChecker.EaseOff(Good()).Status);
        var c = HealthChecker.EaseOff(Good() with { EasedOffNow = EaseOffReasons.Battery });
        Assert.Equal(HealthStatuses.Ok, c.Status);
        Assert.Contains("battery", c.Detail);
        Assert.Equal(HealthFixes.OpenPerformance, c.FixAction);
        Assert.Equal("portal-ai-host:settings/performance", c.FixTarget);
    }

    [Fact]
    public void Memory_cap_fallback_warns_with_its_reason()
    {
        Assert.Equal(HealthStatuses.Ok, HealthChecker.MemoryCap(Good()).Status);
        var c = HealthChecker.MemoryCap(Good() with { MemoryCapActive = false, MemoryCapReason = "Started without the cap." });
        Assert.Equal(HealthStatuses.Warn, c.Status);
        Assert.Equal("Started without the cap.", c.Detail);
    }

    [Fact]
    public void The_report_status_is_the_worst_check_and_counts_what_needs_attention()
    {
        var warn = HealthChecker.Evaluate(Good() with { MemoryCapActive = false, MemoryCapReason = "x" });
        Assert.Equal((HealthStatuses.Warn, 1), (warn.Status, warn.NeedsAttention));

        var fail = HealthChecker.Evaluate(Good() with
        {
            MemoryCapActive = false, MemoryCapReason = "x", Model = Ready with { DamagedReason = "x" },
        });
        Assert.Equal((HealthStatuses.Fail, 2), (fail.Status, fail.NeedsAttention));
    }

    [Fact]
    public void Every_fix_is_an_existing_operation()
    {
        string[] allowed =
        [
            HealthFixes.LoadModel, HealthFixes.RestartEngine, HealthFixes.CheckFiles, HealthFixes.OpenPerformance,
            HealthFixes.OpenModels,
        ];
        var worst = HealthChecker.Evaluate(new HealthInputs
        {
            ApiListening = false, EngineFiles = [new("cpu", true), new("vulkan", false)], GpuAvailable = true,
            ComputeEffective = "cpu", Model = Ready with { DamagedReason = "x", EngineState = EngineState.Failed },
            StoreFreeBytes = 1, Fit = PerformanceFits.TooBig, EasedOffNow = "gpu_busy", MemoryCapActive = false,
        });
        Assert.All(worst.Checks.Where(c => c.FixAction is not null), c => Assert.Contains(c.FixAction!, allowed));
    }
}
