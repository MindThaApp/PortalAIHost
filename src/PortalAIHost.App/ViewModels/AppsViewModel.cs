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

    public ClientRowViewModel(AppsViewModel owner, IStrings s, ClientSummary c)
    {
        _owner = owner;
        _s = s;
        TokenId = c.TokenId;
        Name = c.AppName;
        AddedText = s.Format("Vm_Added", c.CreatedAt.LocalDateTime.ToString("d"));
        LastUsedText = c.LastUsedAt is { } t ? s.Format("Vm_LastUsed", t.LocalDateTime.ToString("g")) : s.Get("Vm_NeverUsed");
    }

    public string TokenId { get; }
    public string Name { get; }
    public string AddedText { get; }
    public string LastUsedText { get; }
    public string AutoRevoke => _s.Format("Vm_AutoRevoke", Name);

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

/// <summary>Plain-language lines for an access request, shared by the prompt and the pending list.</summary>
public static class ApprovalText
{
    public static string Identity(IStrings s, PendingApproval a) => a.CallerVerified
        ? s.Format("Vm_Verified", a.CallerIdentity)
        : s.Format("Vm_Unverified", a.CallerIdentity);

    /// <summary>For the access prompt: a verified app is just called a verified Portal app; the raw id goes under Details.</summary>
    public static string IdentityShort(IStrings s, PendingApproval a) => a.CallerVerified
        ? s.Get("Vm_VerifiedShort")
        : s.Format("Vm_Unverified", a.CallerIdentity);

    /// <summary>The publisher line, or empty when the app is verified and has no publisher to show.</summary>
    public static string PublisherLine(IStrings s, PendingApproval a) =>
        a.CallerVerified && string.IsNullOrWhiteSpace(a.Publisher) ? "" : Publisher(s, a);

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
            _ui.Post(() =>
            {
                ErrorLine = "";
                Clients.Clear();
                // An app waiting out its undo time stays hidden; the host has not been asked yet.
                foreach (var c in clients.Where(c => !c.Revoked && c.TokenId != _pendingRevoke?.TokenId)) Clients.Add(new ClientRowViewModel(this, _s, c));
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
