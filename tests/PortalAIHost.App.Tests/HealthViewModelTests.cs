using Portal.AiHost.Contracts;
using PortalAIHost.App.Services;
using PortalAIHost.App.ViewModels;

namespace PortalAIHost.App.Tests;

public class HealthViewModelTests
{
    private static (FakeHostControl Host, HealthViewModel Health, List<string> Pages) Make(FakeScenario scenario = FakeScenario.Loaded)
    {
        var host = new FakeHostControl(scenario, autoTick: false);
        var health = new HealthViewModel(host, new InlineDispatcher(), new KeyStrings());
        var pages = new List<string>();
        health.Navigate = pages.Add;
        return (host, health, pages);
    }

    private static HealthReport Report(params HealthCheck[] checks) => new()
    {
        Status = checks.Any(c => c.Status == HealthStatuses.Fail) ? HealthStatuses.Fail
            : checks.Any(c => c.Status == HealthStatuses.Warn) ? HealthStatuses.Warn : HealthStatuses.Ok,
        NeedsAttention = checks.Count(c => c.Status != HealthStatuses.Ok),
        Checks = [.. checks],
    };

    private static HealthCheck Check(string status, string? fix = null, string? target = null) => new()
    {
        Id = "x", Status = status, Title = "T", Detail = "D", FixAction = fix, FixTarget = target,
    };

    [Fact]
    public async Task The_loaded_sample_shows_one_warning()
    {
        var (_, health, _) = Make();
        await health.RefreshAsync(force: true);
        Assert.True(health.HasReport);
        Assert.False(health.AllGood);
        Assert.True(health.HasWarn);
        Assert.False(health.HasFail);
        Assert.Equal("Vm_HealthOne", health.Summary);
        Assert.Equal(8, health.Rows.Count);
        Assert.Single(health.Rows, r => r.IsWarn);
    }

    [Fact]
    public async Task All_ok_says_all_good_and_no_row_has_a_fix_button()
    {
        var (host, health, _) = Make();
        host.HealthOverride = Report(Check(HealthStatuses.Ok), Check(HealthStatuses.Ok, HealthFixes.LoadModel));
        await health.RefreshAsync(force: true);
        Assert.True(health.AllGood);
        Assert.Equal("Vm_HealthAllGood", health.Summary);
        Assert.All(health.Rows, r => Assert.False(r.HasFix));
    }

    [Fact]
    public async Task Several_problems_are_counted_and_a_failure_wins_over_a_warning()
    {
        var (host, health, _) = Make();
        host.HealthOverride = Report(Check(HealthStatuses.Warn), Check(HealthStatuses.Fail), Check(HealthStatuses.Ok));
        await health.RefreshAsync(force: true);
        Assert.Equal("Vm_HealthMany:2", health.Summary);
        Assert.True(health.HasFail);
        Assert.False(health.HasWarn);
    }

    [Fact]
    public async Task A_host_that_is_not_running_shows_no_card()
    {
        var (_, health, _) = Make(FakeScenario.NoHost);
        await health.RefreshAsync(force: true);
        Assert.False(health.HasReport);
    }

    [Fact]
    public async Task Reads_closer_than_five_seconds_apart_are_skipped_unless_forced()
    {
        var (host, health, _) = Make();
        await health.RefreshAsync(force: true);
        await health.RefreshAsync();
        Assert.Equal(1, host.HealthCalls);
        await health.RefreshAsync(force: true);
        Assert.Equal(2, host.HealthCalls);
    }

    [Fact]
    public async Task Check_files_fix_verifies_that_model_goes_to_models_and_reads_again()
    {
        var (host, health, pages) = Make();
        var damaged = Check(HealthStatuses.Fail, HealthFixes.CheckFiles, "chat-14b");
        host.HealthOverride = Report(damaged);
        await health.RefreshAsync(force: true);
        Assert.True(health.Rows[0].HasFix);
        Assert.Equal("Vm_HealthFixCheckFiles", health.Rows[0].FixLabel);

        await health.Rows[0].FixCommand.ExecuteAsync(null);
        Assert.Equal(["chat-14b"], host.Verifies);
        Assert.Equal(["models"], pages);
        Assert.Equal(2, host.HealthCalls);
    }

    [Fact]
    public async Task Restart_fix_unloads_then_loads()
    {
        var (host, health, _) = Make();
        await health.FixAsync(Check(HealthStatuses.Fail, HealthFixes.RestartEngine, "m"));
        Assert.Equal(1, host.UnloadCalls);
        Assert.Equal(1, host.LoadCalls);
    }

    [Fact]
    public async Task Load_fix_loads_and_a_failure_is_shown_not_thrown()
    {
        var (host, health, _) = Make();
        host.EngineFailure = "no memory";
        await health.FixAsync(Check(HealthStatuses.Warn, HealthFixes.LoadModel));
        Assert.True(health.HasError);
        Assert.Equal("Vm_HealthFixFailed:no memory", health.Error);
        Assert.False(health.Busy);
    }

    [Fact]
    public async Task Settings_and_models_fixes_only_navigate()
    {
        var (host, health, pages) = Make();
        await health.FixAsync(Check(HealthStatuses.Warn, HealthFixes.OpenPerformance, "portal-ai-host:settings/performance"));
        await health.FixAsync(Check(HealthStatuses.Warn, HealthFixes.OpenModels));
        Assert.Equal(["settings/performance", "models"], pages);
        Assert.Equal(0, host.LoadCalls + host.UnloadCalls);
        Assert.Empty(host.Verifies);
    }

    [Fact]
    public void The_settings_link_becomes_a_page_tag()
    {
        Assert.Equal("settings/performance", HealthViewModel.PageFor("portal-ai-host:settings/performance"));
        Assert.Equal("settings/performance", HealthViewModel.PageFor(null));
    }
}
