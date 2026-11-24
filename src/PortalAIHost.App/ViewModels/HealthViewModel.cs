using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Portal.AiHost.Contracts;
using PortalAIHost.App.Services;

namespace PortalAIHost.App.ViewModels;

/// <summary>One line of the health list: what was checked, what was found, and the fix button when there is one.</summary>
public sealed class HealthRow
{
    public HealthRow(HealthCheck check, string? fixLabel, IAsyncRelayCommand fixCommand)
    {
        Check = check;
        Title = check.Title;
        Detail = check.Detail;
        IsOk = check.Status == HealthStatuses.Ok;
        IsWarn = check.Status == HealthStatuses.Warn;
        IsFail = !IsOk && !IsWarn;
        FixLabel = fixLabel ?? "";
        HasFix = fixLabel is not null && !IsOk;
        FixCommand = fixCommand;
    }

    public HealthCheck Check { get; }
    public string Title { get; }
    public string Detail { get; }
    public bool IsOk { get; }
    public bool IsWarn { get; }
    public bool IsFail { get; }
    public string FixLabel { get; }

    /// <summary>A fix button shows only for a check that needs attention (a loaded model needs no "Load" button).</summary>
    public bool HasFix { get; }

    public IAsyncRelayCommand FixCommand { get; }
}

/// <summary>
/// The health card on Home: "All good" or "N things need attention", with the checks behind an expander and a
/// one-click fix where the host has one. Every fix is an operation the window already has (load, check files,
/// restart, open a settings page); nothing here deletes or changes a file.
/// </summary>
public sealed partial class HealthViewModel : ObservableObject
{
    /// <summary>Reads are at most this often unless the user asks for a new check.</summary>
    public static readonly TimeSpan MinimumGap = TimeSpan.FromSeconds(5);

    private readonly IHostControl _host;
    private readonly IUiDispatcher _ui;
    private readonly IStrings _s;
    private readonly TimeProvider _time;
    private int _version;
    private DateTimeOffset _lastRead = DateTimeOffset.MinValue;

    public HealthViewModel(IHostControl host, IUiDispatcher ui, IStrings strings, TimeProvider? time = null)
    {
        _host = host;
        _ui = ui;
        _s = strings;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Asked to open a page, for example "models" or "settings/performance".</summary>
    public Action<string>? Navigate { get; set; }

    public ObservableCollection<HealthRow> Rows { get; } = new();

    [ObservableProperty] private bool _hasReport;
    [ObservableProperty] private bool _allGood = true;
    [ObservableProperty] private bool _hasWarn;
    [ObservableProperty] private bool _hasFail;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheck))]
    private bool _busy;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _error = "";

    public bool CanCheck => !Busy;

    /// <summary>Runs the checks. Without <paramref name="force"/> a read less than five seconds after the last is skipped.</summary>
    public async Task RefreshAsync(bool force = false)
    {
        var now = _time.GetUtcNow();
        if (!force && now - _lastRead < MinimumGap) return;
        _lastRead = now;
        var version = Interlocked.Increment(ref _version);
        HealthReport? report = null;
        try
        {
            report = await _host.GetHealthAsync().ConfigureAwait(false);
        }
        catch (HostUnavailableException)
        {
        }
        catch (HostRequestException)
        {
        }

        _ui.Post(() =>
        {
            if (version != _version) return;
            if (report is null)
            {
                HasReport = false;
                return;
            }

            Apply(report);
        });
    }

    private void Apply(HealthReport report)
    {
        Rows.Clear();
        foreach (var check in report.Checks)
        {
            var captured = check;
            Rows.Add(new HealthRow(check, LabelFor(check.FixAction), new AsyncRelayCommand(() => FixAsync(captured))));
        }

        var needs = report.Checks.Count(c => c.Status != HealthStatuses.Ok);
        HasFail = report.Checks.Any(c => c.Status == HealthStatuses.Fail);
        HasWarn = !HasFail && needs > 0;
        AllGood = needs == 0;
        Summary = needs == 0 ? _s.Get("Vm_HealthAllGood")
            : needs == 1 ? _s.Get("Vm_HealthOne")
            : _s.Format("Vm_HealthMany", needs);
        HasReport = true;
    }

    private string? LabelFor(string? fix) => fix switch
    {
        HealthFixes.LoadModel => _s.Get("Vm_HealthFixLoad"),
        HealthFixes.RestartEngine => _s.Get("Vm_HealthFixRestart"),
        HealthFixes.CheckFiles => _s.Get("Vm_HealthFixCheckFiles"),
        HealthFixes.OpenPerformance => _s.Get("Vm_HealthFixPerformance"),
        HealthFixes.OpenModels => _s.Get("Vm_HealthFixModels"),
        _ => null,
    };

    /// <summary>Runs the fix of one check. Only calls that already exist; the host answers, then the checks run again.</summary>
    public async Task FixAsync(HealthCheck check)
    {
        if (Busy || check.FixAction is null) return;
        HasError = false;
        Error = "";
        switch (check.FixAction)
        {
            case HealthFixes.OpenModels:
                Navigate?.Invoke("models");
                return;
            case HealthFixes.OpenPerformance:
                Navigate?.Invoke(PageFor(check.FixTarget));
                return;
        }

        Busy = true;
        string? failure = null;
        try
        {
            await Task.Yield(); // the busy state shows before the call goes out
            switch (check.FixAction)
            {
                case HealthFixes.LoadModel:
                    await _host.LoadEngineAsync().ConfigureAwait(false);
                    break;
                case HealthFixes.RestartEngine:
                    await _host.UnloadEngineAsync().ConfigureAwait(false);
                    await _host.LoadEngineAsync().ConfigureAwait(false);
                    break;
                case HealthFixes.CheckFiles when check.FixTarget is { Length: > 0 } id:
                    await _host.VerifyModelAsync(id).ConfigureAwait(false);
                    break;
            }
        }
        catch (HostUnavailableException)
        {
            failure = _s.Get("Vm_NoHostTitle");
        }
        catch (HostRequestException ex)
        {
            failure = ex.Message;
        }

        _ui.Post(() =>
        {
            Busy = false;
            if (failure is null) return;
            Error = _s.Format("Vm_HealthFixFailed", failure);
            HasError = true;
        });
        if (failure is not null) return;

        if (check.FixAction == HealthFixes.CheckFiles) Navigate?.Invoke("models"); // the check reports its progress there
        await RefreshAsync(force: true).ConfigureAwait(false);
    }

    /// <summary>"portal-ai-host:settings/performance" becomes the page tag "settings/performance".</summary>
    internal static string PageFor(string? link)
    {
        const string scheme = "portal-ai-host:";
        return link is not null && link.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)
            ? link[scheme.Length..]
            : "settings/performance";
    }

    [RelayCommand]
    private Task CheckAgainAsync() => RefreshAsync(force: true);
}
