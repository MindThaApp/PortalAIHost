using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Portal.AiHost.Contracts.Control;
using PortalAIHost.App.Services;

namespace PortalAIHost.App.ViewModels;

/// <summary>One approved app.</summary>
public sealed partial class ClientRowViewModel : ObservableObject
{
    private readonly AppsViewModel _owner;
    private readonly IStrings _s;

    private bool _applying;

    public ClientRowViewModel(AppsViewModel owner, IStrings s, ClientSummary c, AppActivity? today = null)
    {
        _owner = owner;
        _s = s;
        TokenId = c.TokenId;
        AppId = c.AppId;
        Name = c.AppName;
        AddedText = s.Format("Vm_Added", c.CreatedAt.LocalDateTime.ToString("d"));
        LastUsedText = c.LastUsedAt is { } t ? s.Format("Vm_LastUsed", t.LocalDateTime.ToString("g")) : s.Get("Vm_NeverUsed");
        TodayText = today is null || today.Requests == 0 ? s.Get("Vm_AppTodayNone")
            : s.Format(today.Requests == 1 ? "Vm_AppToday1" : "Vm_AppToday", today.Requests);
        PriorityOptions = [s.Get("Vm_PriorityHigh"), s.Get("Vm_PriorityNormal"), s.Get("Vm_PriorityLow")];
        _applying = true;
        _isPaused = c.Paused;
        _priorityIndex = ConfirmedPriorityIndex = IndexOf(c.Priority);
        _applying = false;
    }

    public static int IndexOf(string? priority) => priority switch
    {
        AppPriorities.High => 0,
        AppPriorities.Low => 2,
        _ => 1,
    };

    public static string WireOf(int index) => index switch
    {
        0 => AppPriorities.High,
        2 => AppPriorities.Low,
        _ => AppPriorities.Normal,
    };

    public string TokenId { get; }
    public string AppId { get; }
    public string Name { get; }
    public string AddedText { get; }
    public string LastUsedText { get; }
    public string TodayText { get; }
    public string AutoRevoke => _s.Format("Vm_AutoRevoke", Name);
    public IReadOnlyList<string> PriorityOptions { get; }

    /// <summary>The priority the host last accepted.</summary>
    internal int ConfirmedPriorityIndex { get; set; }
    public string PriorityAutomation => _s.Format("Vm_AutoPriority", Name);
    public string PausedBadge => _s.Get("Vm_AppPausedBadge");
    public string PausedHint => _s.Format("Vm_AppPausedHint", Name);
    public string PauseLabel => _s.Get(IsPaused ? "Vm_AppResume" : "Vm_AppPause");
    public string PauseAutomation => _s.Format(IsPaused ? "Vm_AutoResume" : "Vm_AutoPause", Name);

    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private int _priorityIndex = 1;

    partial void OnIsPausedChanged(bool value)
    {
        OnPropertyChanged(nameof(PauseLabel));
        OnPropertyChanged(nameof(PauseAutomation));
    }

    partial void OnPriorityIndexChanged(int value)
    {
        if (!_applying) _ = _owner.SetControlAsync(this, null, WireOf(value));
    }

    /// <summary>Puts a value back after the host refused it, without sending it again.</summary>
    internal void Restore(bool paused, int priorityIndex)
    {
        _applying = true;
        IsPaused = paused;
        PriorityIndex = priorityIndex;
        _applying = false;
    }

    [RelayCommand] private Task TogglePauseAsync()
    {
        var next = !IsPaused;
        IsPaused = next;
        return _owner.SetControlAsync(this, next, null);
    }

    [RelayCommand] private Task RevokeAsync() => _owner.RevokeAsync(this);
}

/// <summary>One access request waiting for an answer.</summary>
public sealed partial class PendingRowViewModel : ObservableObject
{
    private readonly AppsViewModel _owner;
    private readonly IStrings _s;

    public PendingRowViewModel(AppsViewModel owner, IStrings s, PendingApproval a)
    {
        _owner = owner;
        _s = s;
        RequestId = a.RequestId;
        Name = a.AppName;
        IdentityText = ApprovalText.Identity(s, a);
    }

    public string RequestId { get; }
    public string Name { get; }
    public string IdentityText { get; }
    public string AutoAllow => _s.Format("Vm_AutoAllow", Name);
    public string AutoDeny => _s.Format("Vm_AutoDeny", Name);

    [RelayCommand] private Task AllowAsync() => _owner.AnswerAsync(this, true);
    [RelayCommand] private Task DenyAsync() => _owner.AnswerAsync(this, false);
}

/// <summary>
/// Plain-language lines for an access request, shared by the prompt and the pending list. Only a caller the host
/// matched to the Portal publisher's Store packages is called a Portal app; any other package is named by its
/// package id, and anything unpackaged gets the warning.
/// </summary>
public static class ApprovalText
{
    /// <summary>True only when the host tied the caller to a Portal package (never from the app's own name).</summary>
    public static bool IsPortalApp(PendingApproval a) => a.CallerVerified && a.CallerPortalApp;

    public static string Identity(IStrings s, PendingApproval a) =>
        IsPortalApp(a) ? s.Format("Vm_VerifiedPortal", a.CallerIdentity)
        : a.CallerVerified ? s.Format("Vm_Verified", a.CallerIdentity)
        : s.Format("Vm_Unverified", a.CallerIdentity);

    /// <summary>For the access prompt: a Portal app is just called a verified Portal app; the raw id goes under Details.</summary>
    public static string IdentityShort(IStrings s, PendingApproval a) =>
        IsPortalApp(a) ? s.Get("Vm_VerifiedShort")
        : a.CallerVerified ? s.Format("Vm_Verified", a.CallerIdentity)
        : s.Format("Vm_Unverified", a.CallerIdentity);

    /// <summary>The publisher line, or empty when the app is a Portal app and has no publisher to show.</summary>
    public static string PublisherLine(IStrings s, PendingApproval a) =>
        IsPortalApp(a) && string.IsNullOrWhiteSpace(a.Publisher) ? "" : Publisher(s, a);

    public static string Publisher(IStrings s, PendingApproval a) => string.IsNullOrWhiteSpace(a.Publisher)
        ? s.Get("Vm_PublisherUnknown")
        : s.Format("Vm_Publisher", a.Publisher);
}

/// <summary>Devices and apps page: who may use the host, and who is asking.</summary>
public sealed partial class AppsViewModel : ObservableObject, IDisposable
{
    /// <summary>How long "Access removed" can be undone before the host is asked to revoke the token.</summary>
    public static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(8);

    private readonly ShellViewModel _shell;
    private readonly IUiDispatcher _ui;
    private readonly IStrings _s;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _gate = new();
    private ClientRowViewModel? _pendingRevoke;
    private int _pendingIndex;
    private CancellationTokenSource? _undoTimer;

    public AppsViewModel(ShellViewModel shell, IUiDispatcher ui, IStrings strings, IDialogService dialogs, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _shell = shell;
        _ui = ui;
        _s = strings;
        _ = dialogs;
        _delay = delay ?? Task.Delay;
        shell.AddFlush(FlushAsync);
    }

    /// <summary>Shown above the list while a removal can still be undone: "Access removed for Notes."</summary>
    [ObservableProperty] private string _revokedLine = "";
    [ObservableProperty] private bool _hasUndo;
    [ObservableProperty] private string _undoName = "";

    /// <summary>Screen-reader name of the Undo button, naming the app.</summary>
    public string UndoLabel => _s.Format("Vm_AutoUndo", UndoName);
    partial void OnUndoNameChanged(string value) => OnPropertyChanged(nameof(UndoLabel));

    public ObservableCollection<ClientRowViewModel> Clients { get; } = new();
    public ObservableCollection<PendingRowViewModel> Pending { get; } = new();

    [ObservableProperty] private bool _noClients;
    [ObservableProperty] private bool _hasPending;
    [ObservableProperty] private string _errorLine = "";

    public async Task LoadAsync()
    {
        try
        {
            var clients = await _shell.Host.ListClientsAsync().ConfigureAwait(false);
            var pending = await _shell.Host.ListApprovalsAsync().ConfigureAwait(false);
            IReadOnlyList<AppActivity> activity = [];
            try { activity = await _shell.Host.GetActivityAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is HostUnavailableException or HostRequestException) { }
            _ui.Post(() =>
            {
                ErrorLine = "";
                Clients.Clear();
                // An app waiting out its undo time stays hidden; the host has not been asked yet.
                foreach (var c in clients.Where(c => !c.Revoked && c.TokenId != _pendingRevoke?.TokenId)) Clients.Add(new ClientRowViewModel(this, _s, c, activity.FirstOrDefault(a => a.AppId == c.AppId)));
                Pending.Clear();
                foreach (var p in pending) Pending.Add(new PendingRowViewModel(this, _s, p));
                NoClients = Clients.Count == 0;
                HasPending = Pending.Count > 0;
            });
        }
        catch (HostUnavailableException)
        {
            _ui.Post(() => ErrorLine = _s.Get("Vm_NoHostTitle"));
        }
    }

    /// <summary>
    /// Removes an app's access. The row disappears at once and "Access removed for X. Undo" shows. The host is
    /// asked to revoke when the undo time ends, or when the page or window closes, because a revoked token can
    /// never be turned back on.
    /// </summary>
    public async Task RevokeAsync(ClientRowViewModel row)
    {
        await FlushAsync().ConfigureAwait(false); // a removal still waiting is made final first
        CancellationTokenSource timer;
        lock (_gate)
        {
            _pendingIndex = Math.Max(0, Clients.IndexOf(row));
            _pendingRevoke = row;
            _undoTimer = timer = new CancellationTokenSource();
        }
        _ui.Post(() =>
        {
            Clients.Remove(row);
            NoClients = Clients.Count == 0;
            UndoName = row.Name;
            RevokedLine = _s.Format("Vm_RevokedBar", row.Name);
            HasUndo = true;
        });
        try
        {
            await _delay(UndoWindow, timer.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // undone, or finished by a flush
        }
        await CommitAsync(row).ConfigureAwait(false);
    }

    /// <summary>Brings the removed app back and cancels the pending revoke. Nothing was sent to the host.</summary>
    [RelayCommand]
    private void Undo()
    {
        ClientRowViewModel? row;
        int index;
        lock (_gate)
        {
            row = _pendingRevoke;
            index = _pendingIndex;
            _pendingRevoke = null;
            _undoTimer?.Cancel();
            _undoTimer = null;
        }
        if (row is null) return;
        _ui.Post(() =>
        {
            Clients.Insert(Math.Min(index, Clients.Count), row);
            NoClients = false;
            HasUndo = false;
            RevokedLine = "";
        });
    }

    /// <summary>Sends any removal that is still waiting. Called when the page or the window closes.</summary>
    public Task FlushAsync()
    {
        ClientRowViewModel? row;
        lock (_gate) row = _pendingRevoke;
        return row is null ? Task.CompletedTask : CommitAsync(row);
    }

    private async Task CommitAsync(ClientRowViewModel row)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_pendingRevoke, row)) return; // undone or already sent
            _pendingRevoke = null;
            _undoTimer?.Cancel();
            _undoTimer = null;
        }
        _ui.Post(() => { HasUndo = false; RevokedLine = ""; });
        try
        {
            await _shell.Host.RevokeClientAsync(row.TokenId).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HostUnavailableException or HostRequestException)
        {
            // The app still has access, so it comes back into the list with the reason.
            _ui.Post(() =>
            {
                ErrorLine = ex is HostRequestException ? ex.Message : _s.Get("Vm_NoHostTitle");
                Clients.Add(row);
                NoClients = false;
            });
            return;
        }
    }

    /// <summary>Sends a pause/resume or priority change. The row already shows it; a refusal puts the old value back.</summary>
    public async Task SetControlAsync(ClientRowViewModel row, bool? paused, string? priority)
    {
        try
        {
            await _shell.Host.SetAppControlAsync(row.AppId, paused, priority).ConfigureAwait(false);
            if (priority is not null) row.ConfirmedPriorityIndex = ClientRowViewModel.IndexOf(priority);
        }
        catch (Exception ex) when (ex is HostUnavailableException or HostRequestException)
        {
            _ui.Post(() =>
            {
                ErrorLine = ex is HostRequestException ? ex.Message : _s.Get("Vm_NoHostTitle");
                row.Restore(paused is { } p ? !p : row.IsPaused,
                    priority is not null ? row.ConfirmedPriorityIndex : row.PriorityIndex);
            });
        }
    }

    public void Dispose() => _shell.RemoveFlush(FlushAsync);

    public async Task AnswerAsync(PendingRowViewModel row, bool allow)
    {
        try
        {
            if (allow) await _shell.Host.ApproveAsync(row.RequestId);
            else await _shell.Host.DenyAsync(row.RequestId);
        }
        catch (HostUnavailableException)
        {
            ErrorLine = _s.Get("Vm_NoHostTitle");
            return;
        }
        await LoadAsync();
    }
}
