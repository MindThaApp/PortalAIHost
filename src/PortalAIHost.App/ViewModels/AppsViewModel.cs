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
}

/// <summary>Devices and apps page: who may use the host, and who is asking.</summary>
public sealed partial class AppsViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly IUiDispatcher _ui;
    private readonly IStrings _s;
    private readonly IDialogService _dialogs;

    public AppsViewModel(ShellViewModel shell, IUiDispatcher ui, IStrings strings, IDialogService dialogs)
    {
        _shell = shell;
        _ui = ui;
        _s = strings;
        _dialogs = dialogs;
    }

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
                foreach (var c in clients.Where(c => !c.Revoked)) Clients.Add(new ClientRowViewModel(this, _s, c));
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

    public async Task RevokeAsync(ClientRowViewModel row)
    {
        var ok = await _dialogs.ConfirmAsync(_s.Format("Vm_RevokeTitle", row.Name), _s.Format("Vm_RevokeBody", row.Name), _s.Get("Vm_RevokeYes"), _s.Get("Vm_Cancel"));
        if (!ok) return;
        try
        {
            await _shell.Host.RevokeClientAsync(row.TokenId);
        }
        catch (HostUnavailableException)
        {
            ErrorLine = _s.Get("Vm_NoHostTitle");
            return;
        }
        await LoadAsync();
    }

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
