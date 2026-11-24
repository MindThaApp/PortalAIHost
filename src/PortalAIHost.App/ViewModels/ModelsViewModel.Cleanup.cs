using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using PortalAIHost.App.Services;

namespace PortalAIHost.App.ViewModels;

/// <summary>One removable item in the "Free up space" list, ticked by default.</summary>
public sealed partial class CleanupItemViewModel : ObservableObject
{
    public CleanupItemViewModel(CleanupItemInfo info)
    {
        Id = info.Id;
        Kind = info.Kind;
        Name = info.Label;
        Bytes = info.Bytes;
        SizeText = Fmt.Bytes(info.Bytes);
        Detail = info.Detail ?? "";
    }

    public string Id { get; }
    public string Kind { get; }
    public string Name { get; }
    public string Detail { get; }
    public long Bytes { get; }
    public string SizeText { get; }

    [ObservableProperty] private bool _selected = true;
}

/// <summary>Models page: the "Free up space" panel on the storage line.</summary>
public sealed partial class ModelsViewModel
{
    /// <summary>Unfinished downloads nothing is resuming.</summary>
    public ObservableCollection<CleanupItemViewModel> CleanupPartial { get; } = new();

    /// <summary>Old copies left after a move.</summary>
    public ObservableCollection<CleanupItemViewModel> CleanupOld { get; } = new();

    /// <summary>Models a check found damaged.</summary>
    public ObservableCollection<CleanupItemViewModel> CleanupDamaged { get; } = new();

    [ObservableProperty] private bool _cleanupOpen;
    [ObservableProperty] private bool _cleanupHasItems;
    [ObservableProperty] private bool _cleanupHasPartial;
    [ObservableProperty] private bool _cleanupHasOld;
    [ObservableProperty] private bool _cleanupHasDamaged;
    [ObservableProperty] private bool _cleanupWorking;

    /// <summary>"Nothing to free up", the busy reason, or the result of the last clean.</summary>
    [ObservableProperty] private string _cleanupLine = "";

    [ObservableProperty] private string _cleanupButtonText = "";
    [ObservableProperty] private bool _canCleanup;

    private IEnumerable<CleanupItemViewModel> AllCleanup => CleanupPartial.Concat(CleanupOld).Concat(CleanupDamaged);

    private void ResetCleanup()
    {
        foreach (var i in AllCleanup) i.PropertyChanged -= OnCleanupItemChanged;
        CleanupPartial.Clear();
        CleanupOld.Clear();
        CleanupDamaged.Clear();
        CleanupHasItems = CleanupHasPartial = CleanupHasOld = CleanupHasDamaged = false;
        CanCleanup = false;
    }

    private void OnCleanupItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => RefreshCleanupTotal();

    private void RefreshCleanupTotal()
    {
        var picked = AllCleanup.Where(i => i.Selected).ToList();
        CanCleanup = picked.Count > 0 && !CleanupWorking;
        CleanupButtonText = _s.Format("Vm_CleanupButton", Fmt.Bytes(picked.Sum(i => i.Bytes)));
    }

    /// <summary>Reads what could be freed (nothing is deleted) and opens the list.</summary>
    [RelayCommand]
    private async Task FreeUpSpaceAsync()
    {
        CleanupScan scan;
        try
        {
            scan = await _shell.Host.ScanCleanupAsync().ConfigureAwait(false);
        }
        catch (HostUnavailableException)
        {
            _ui.Post(() => ErrorLine = _s.Get("Vm_NoHostTitle"));
            return;
        }
        catch (HostRequestException ex)
        {
            _ui.Post(() => ErrorLine = ex.Message);
            return;
        }

        _ui.Post(() =>
        {
            ResetCleanup();
            CleanupOpen = true;
            if (scan.BusyReason is { Length: > 0 } busy)
            {
                CleanupLine = busy;
                return;
            }

            foreach (var item in scan.Items)
            {
                var vm = new CleanupItemViewModel(item);
                vm.PropertyChanged += OnCleanupItemChanged;
                (item.Kind switch
                {
                    CleanupKinds.OldCopies => CleanupOld,
                    CleanupKinds.Damaged => CleanupDamaged,
                    _ => CleanupPartial,
                }).Add(vm);
            }

            CleanupHasPartial = CleanupPartial.Count > 0;
            CleanupHasOld = CleanupOld.Count > 0;
            CleanupHasDamaged = CleanupDamaged.Count > 0;
            CleanupHasItems = AllCleanup.Any();
            CleanupLine = CleanupHasItems ? "" : _s.Get("Vm_CleanupNothing");
            RefreshCleanupTotal();
        });
    }

    [RelayCommand]
    private void CloseCleanup()
    {
        ResetCleanup();
        CleanupOpen = false;
    }

    /// <summary>Asks once, naming what goes, then has the host remove the ticked items (it checks each again before deleting).</summary>
    [RelayCommand]
    private async Task ConfirmCleanupAsync()
    {
        var picked = AllCleanup.Where(i => i.Selected).ToList();
        if (picked.Count == 0 || CleanupWorking) return;
        var names = string.Join("\n", picked.Select(i => "• " + i.Name + " (" + i.SizeText + ")"));
        var total = Fmt.Bytes(picked.Sum(i => i.Bytes));
        var ok = await _dialogs.ConfirmAsync(_s.Get("Vm_CleanupConfirmTitle"),
            _s.Format("Vm_CleanupConfirmBody", names), _s.Format("Vm_CleanupButton", total), _s.Get("Vm_Cancel"));
        if (!ok) return;

        _ui.Post(() => { CleanupWorking = true; CanCleanup = false; CleanupLine = _s.Get("Vm_CleanupRunning"); });
        CleanupResult? result = null;
        string? error = null;
        try
        {
            result = await _shell.Host.CleanupAsync([.. picked.Select(i => i.Id)]).ConfigureAwait(false);
        }
        catch (HostUnavailableException)
        {
            error = _s.Get("Vm_NoHostTitle");
        }
        catch (HostRequestException ex)
        {
            error = ex.Message;
        }

        _ui.Post(() =>
        {
            CleanupWorking = false;
            ResetCleanup();
            CleanupOpen = true;
            CleanupLine = error is not null
                ? _s.Format("Vm_CleanupFailed", error)
                : result!.Skipped.Count == 0
                    ? _s.Format("Vm_CleanupDone", Fmt.Bytes(result.FreedBytes))
                    : _s.Format("Vm_CleanupDoneSkipped", Fmt.Bytes(result.FreedBytes),
                        string.Join("\n", result.Skipped.Select(s => "• " + s.Label + ": " + s.Reason)));
        });
        _ = LoadAsync();
    }
}
