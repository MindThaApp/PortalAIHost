using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using PortalAIHost.App.Services;

namespace PortalAIHost.App.ViewModels;

/// <summary>One line of the "Today" list: an app, its request count and when it was last seen.</summary>
public sealed record TodayRow(string Name, string Detail);

/// <summary>
/// What is happening in the host right now: who is generating, the queue, memory use, and today's per-app counts.
/// Shared by the Home page and the tray pop-up. It reads from the host only while <see cref="Start"/> is active
/// (every 1.5 s), so a hidden page or pop-up costs nothing.
/// Unload and Load follow "visual first, work after": the pending state shows in the same frame, the host call
/// runs after, and a failure rolls the state back and shows the reason.
/// </summary>
public sealed partial class LiveViewModel : ObservableObject, IDisposable
{
    /// <summary>How often the host is read while the page or pop-up is visible.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(1500);

    private const double WordsPerToken = 0.75;

    private readonly IHostControl _host;
    private readonly IUiDispatcher _ui;
    private readonly IStrings _s;
    private readonly TimeProvider _time;
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _loop;
    private int _refreshVersion;

    public LiveViewModel(IHostControl host, IUiDispatcher ui, IStrings strings, TimeProvider? time = null, TimeSpan? interval = null)
    {
        _host = host;
        _ui = ui;
        _s = strings;
        _time = time ?? TimeProvider.System;
        _interval = interval ?? RefreshInterval;
        ApplyOffline();
    }

    /// <summary>Latest status the host sent, or null when it is not running.</summary>
    public HostStatus? Status { get; private set; }

    public ObservableCollection<TodayRow> Today { get; } = new();

    /// <summary>Raised after every refresh, on the UI thread.</summary>
    public event Action? Updated;

    /// <summary>The app seen most recently today, or null.</summary>
    public AppActivity? LastRequest { get; private set; }

    // Activity card.
    [ObservableProperty] private bool _isGenerating;
    [ObservableProperty] private string _activityTitle = "";

    /// <summary>Names of the apps generating now, joined; empty when none.</summary>
    public string ActivityTitleApps { get; private set; } = "";
    [ObservableProperty] private string _speedLine = "";
    [ObservableProperty] private bool _hasSpeed;
    [ObservableProperty] private string _queueLine = "";

    // Meters: the engine's share of the PC's memory and graphics memory.
    [ObservableProperty] private bool _hasMemory;
    [ObservableProperty] private double _ramPercent;
    [ObservableProperty] private string _ramLine = "";
    [ObservableProperty] private bool _hasVram;
    [ObservableProperty] private double _vramPercent;
    [ObservableProperty] private string _vramLine = "";
    [ObservableProperty] private string _ramName = "";
    [ObservableProperty] private string _vramName = "";

    // Unload and Load.
    [ObservableProperty] private bool _canUnload;
    [ObservableProperty] private bool _canLoad;
    [ObservableProperty] private bool _actionPending;
    [ObservableProperty] private string _pendingText = "";
    [ObservableProperty] private bool _hasActionError;
    [ObservableProperty] private string _actionError = "";

    // Today list.
    [ObservableProperty] private bool _hasToday;
    [ObservableProperty] private bool _noToday = true;
    [ObservableProperty] private string _todayEmptyText = "";

    /// <summary>Starts reading every 1.5 s. Safe to call twice.</summary>
    public void Start()
    {
        if (_loop is not null) return;
        var cts = new CancellationTokenSource();
        _loop = cts;
        _ = RunAsync(cts.Token);
    }

    /// <summary>Stops reading. Call when the page or pop-up is hidden.</summary>
    public void Stop()
    {
        var cts = Interlocked.Exchange(ref _loop, null);
        cts?.Cancel();
        cts?.Dispose();
    }

    public bool IsRunning => Volatile.Read(ref _loop) is not null;

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(_interval, _time);
            do
            {
                await RefreshAsync().ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Reads status and today's activity once and updates every property.</summary>
    public async Task RefreshAsync()
    {
        var version = Interlocked.Increment(ref _refreshVersion);
        HostSnapshot snap;
        IReadOnlyList<AppActivity> activity = [];
        try
        {
            snap = await _host.GetStatusAsync().ConfigureAwait(false);
            if (snap.Connected) activity = await _host.GetActivityAsync().ConfigureAwait(false);
        }
        catch (HostUnavailableException)
        {
            snap = HostSnapshot.NoHost;
        }
        catch (HostRequestException)
        {
            return; // keep what is on screen
        }
        _ui.Post(() =>
        {
            if (version != Volatile.Read(ref _refreshVersion)) return;
            Apply(snap.Status, activity);
        });
    }

    private void ApplyOffline() => Apply(null, []);

    private void Apply(HostStatus? status, IReadOnlyList<AppActivity> activity)
    {
        Status = status;
        var generating = activity.Where(a => a.Running > 0).ToList();
        var chat = ChatOf(status);

        IsGenerating = generating.Count > 0;
        ActivityTitleApps = string.Join(", ", generating.Select(a => a.AppName));
        ActivityTitle = generating.Count > 0
            ? _s.Format("Vm_LiveGenerating", string.Join(", ", generating.Select(a => a.AppName)))
            : _s.Get("Vm_LiveIdle");
        var tps = status?.Usage?.GenerationTokensPerSecond;
        HasSpeed = generating.Count > 0 && tps is > 0;
        SpeedLine = HasSpeed ? _s.Format("Vm_LiveSpeed", Math.Max(1, (int)Math.Round(tps!.Value * WordsPerToken))) : "";
        QueueLine = status is null || status.Queue.Running + status.Queue.Waiting == 0
            ? _s.Get("Vm_QueueEmpty")
            : _s.Format("Vm_QueueSome", status.Queue.Running, status.Queue.Waiting);

        var ramTotal = status?.Hardware.RamBytes ?? 0;
        var ramUsed = status?.Usage?.RamUsedBytes ?? 0;
        HasMemory = status?.Usage is not null && chat?.State is ModelStates.Loaded or ModelStates.Loading;
        RamPercent = Share(ramUsed, ramTotal);
        RamLine = ramTotal > 0
            ? _s.Format("Vm_LiveMemory", Fmt.Bytes(ramUsed), Fmt.Bytes(ramTotal))
            : _s.Format("Vm_LiveMemoryNoTotal", Fmt.Bytes(ramUsed));

        var vramTotal = status?.Hardware.Gpus.Sum(g => g.VramBytes) ?? 0;
        var vramUsed = status?.Usage?.VramUsedBytes ?? 0;
        HasVram = HasMemory && vramTotal > 0;
        VramPercent = Share(vramUsed, vramTotal);
        VramLine = _s.Format("Vm_LiveMemory", Fmt.Bytes(vramUsed), Fmt.Bytes(vramTotal));
        RamName = _s.Format("Vm_LiveRamName", RamLine);
        VramName = _s.Format("Vm_LiveVramName", VramLine);

        var hasModel = chat is not null && chat.State != ModelStates.NotInstalled;
        if (!ActionPending)
        {
            CanUnload = chat?.State == ModelStates.Loaded;
            CanLoad = hasModel && chat!.State is ModelStates.Unloaded or ModelStates.Failed;
        }

        Today.Clear();
        foreach (var a in activity.Where(a => a.Requests > 0).OrderByDescending(a => a.LastSeen))
            Today.Add(new TodayRow(a.AppName, TodayDetail(a)));
        HasToday = Today.Count > 0;
        NoToday = !HasToday;
        TodayEmptyText = _s.Get("Vm_TodayNone");
        LastRequest = activity.Where(a => a.Requests > 0 && a.LastSeen is not null).MaxBy(a => a.LastSeen);
        Updated?.Invoke();
    }

    private static ModelStatus? ChatOf(HostStatus? status) =>
        status is not null && status.Models.TryGetValue(ModelKinds.Chat, out var m) ? m : null;

    private static double Share(long used, long total) => total <= 0 ? 0 : Math.Clamp(100.0 * used / total, 0, 100);

    private string TodayDetail(AppActivity a) => _s.Format(a.Requests == 1 ? "Vm_TodayRow1" : "Vm_TodayRow", a.Requests, SeenText(a.LastSeen));

    /// <summary>"just now", "5 min ago" or "3 h ago".</summary>
    public string SeenText(DateTimeOffset? seen)
    {
        if (seen is not { } at) return _s.Get("Vm_SeenNever");
        var age = _time.GetUtcNow() - at;
        if (age < TimeSpan.FromMinutes(1)) return _s.Get("Vm_SeenNow");
        if (age < TimeSpan.FromHours(1)) return _s.Format("Vm_SeenMinutes", (int)age.TotalMinutes);
        return _s.Format("Vm_SeenHours", (int)age.TotalHours);
    }

    [RelayCommand]
    private Task UnloadAsync() => EngineActionAsync(load: false);

    [RelayCommand]
    private Task LoadAsync() => EngineActionAsync(load: true);

    private async Task EngineActionAsync(bool load)
    {
        if (ActionPending || (load ? !CanLoad : !CanUnload)) return;
        // Same frame: the buttons go pending before any work starts.
        var wasUnload = CanUnload;
        var wasLoad = CanLoad;
        ActionPending = true;
        CanUnload = false;
        CanLoad = false;
        PendingText = _s.Get(load ? "Vm_LivePendingLoad" : "Vm_LivePendingUnload");
        HasActionError = false;
        ActionError = "";
        string? failure = null;
        try
        {
            await Task.Yield(); // let the pending state render before the call goes out
            if (load) await _host.LoadEngineAsync().ConfigureAwait(false);
            else await _host.UnloadEngineAsync().ConfigureAwait(false);
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
            ActionPending = false;
            PendingText = "";
            if (failure is not null)
            {
                // Roll back to what was shown before, and say why.
                CanUnload = wasUnload;
                CanLoad = wasLoad;
                ActionError = _s.Format(load ? "Vm_LiveLoadFailed" : "Vm_LiveUnloadFailed", failure);
                HasActionError = true;
            }
        });
        if (failure is null) await RefreshAsync().ConfigureAwait(false);
    }

    public void Dispose() => Stop();
}
