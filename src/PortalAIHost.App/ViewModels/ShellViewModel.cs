using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Portal.AiHost.Contracts.Control;
using PortalAIHost.App.Services;

namespace PortalAIHost.App.ViewModels;

/// <summary>
/// Holds the live connection to the host for the whole window: latest status, the start-the-host attempt,
/// and the queue of access requests waiting for the user's answer.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly IHostControl _host;
    private readonly IHostLauncher _launcher;
    private readonly IUiDispatcher _ui;
    private readonly IStrings _strings;
    private readonly Queue<PendingApproval> _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _pumping;

    public ShellViewModel(IHostControl host, IHostLauncher launcher, IUiDispatcher ui, IStrings strings, IAppState state)
    {
        _host = host;
        _launcher = launcher;
        _ui = ui;
        _strings = strings;
        State = state;
        host.StatusChanged += s => _ui.Post(() => Apply(s));
        host.ApprovalPending += a => _ui.Post(() => Enqueue(a));
    }

    public IAppState State { get; }
    public IHostControl Host => _host;

    [ObservableProperty]
    private HostSnapshot _snapshot = HostSnapshot.NoHost;

    [ObservableProperty]
    private bool _starting;

    [ObservableProperty]
    private string _startMessage = "";

    /// <summary>True until first-run setup has been finished or skipped.</summary>
    [ObservableProperty]
    private bool _showFirstRun;

    public bool Connected => Snapshot.Connected;

    public event Action? SnapshotChanged;

    /// <summary>Set by the window: shows the approval prompt. Returns true for Allow, false for Deny, null when closed without an answer.</summary>
    public Func<PendingApproval, Task<bool?>>? ApprovalPresenter { get; set; }

    public async Task InitializeAsync()
    {
        await RefreshAsync().ConfigureAwait(false);
        if (!Snapshot.Connected)
            await StartHostAsync().ConfigureAwait(false);
        _ui.Post(() => ShowFirstRun = !State.FirstRunDone && NeedsSetup(Snapshot));
        _ = PollAsync();
    }

    private static bool NeedsSetup(HostSnapshot s) =>
        s.Connected && s.Status is { } st && !st.Models.Values.Any(m => m.State != Portal.AiHost.Contracts.ModelStates.NotInstalled);

    public async Task RefreshAsync()
    {
        HostSnapshot s;
        try
        {
            s = await _host.GetStatusAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (HostUnavailableException)
        {
            s = HostSnapshot.NoHost;
        }
        _ui.Post(() => Apply(s));
    }

    private async Task PollAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
                await RefreshAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Tries to start the host, then checks again. Safe to call when it is already running.</summary>
    public async Task StartHostAsync()
    {
        _ui.Post(() => { Starting = true; StartMessage = _strings.Get("Vm_Starting"); });
        var requested = false;
        try
        {
            requested = await _launcher.StartHostAsync(_cts.Token).ConfigureAwait(false);
            if (requested) await RefreshAsync().ConfigureAwait(false);
        }
        finally
        {
            _ui.Post(() =>
            {
                Starting = false;
                StartMessage = Snapshot.Connected ? "" : _strings.Get("Vm_StartFailed");
            });
        }
    }

    private void Apply(HostSnapshot s)
    {
        var wasConnected = Snapshot.Connected;
        Snapshot = s;
        OnPropertyChanged(nameof(Connected));
        SnapshotChanged?.Invoke();
        if (s.Connected && !wasConnected)
            _ = LoadPendingAsync();
    }

    private async Task LoadPendingAsync()
    {
        try
        {
            var list = await _host.ListApprovalsAsync(_cts.Token).ConfigureAwait(false);
            _ui.Post(() => { foreach (var a in list) Enqueue(a); });
        }
        catch (HostUnavailableException)
        {
        }
    }

    private void Enqueue(PendingApproval a)
    {
        if (_queue.Any(q => q.RequestId == a.RequestId) || Current?.RequestId == a.RequestId) return;
        _queue.Enqueue(a);
        PendingCount = _queue.Count + (Current is null ? 0 : 1);
        _ = PumpApprovalsAsync();
    }

    public PendingApproval? Current { get; private set; }

    [ObservableProperty]
    private int _pendingCount;

    /// <summary>Shows one approval prompt at a time, in the order asked, and sends the answer to the host.</summary>
    public async Task PumpApprovalsAsync()
    {
        if (_pumping || ApprovalPresenter is null) return;
        _pumping = true;
        try
        {
            while (_queue.Count > 0)
            {
                var next = _queue.Dequeue();
                Current = next;
                PendingCount = _queue.Count + 1;
                if (next.ExpiresAt < DateTimeOffset.Now) continue;
                var answer = await ApprovalPresenter(next);
                try
                {
                    if (answer == true) await _host.ApproveAsync(next.RequestId, _cts.Token);
                    else if (answer == false) await _host.DenyAsync(next.RequestId, _cts.Token);
                }
                catch (HostUnavailableException)
                {
                }
            }
        }
        finally
        {
            Current = null;
            PendingCount = 0;
            _pumping = false;
        }
        await RefreshAsync();
    }

    public void CompleteFirstRun()
    {
        State.MarkFirstRunDone();
        ShowFirstRun = false;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
