using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using PortalAIHost.App.Services;

namespace PortalAIHost.App.ViewModels;

/// <summary>One model in the list, with its download progress.</summary>
public sealed partial class ModelRowViewModel : ObservableObject
{
    private readonly ModelsViewModel _owner;
    private readonly IStrings _s;

    public ModelRowViewModel(ModelsViewModel owner, IStrings strings, CatalogueModelSummary m, bool existingCopy)
    {
        _owner = owner;
        _s = strings;
        Id = m.Id;
        Name = m.DisplayName;
        SizeBytes = m.SizeBytes;
        SizeText = Fmt.Bytes(m.SizeBytes);
        QualityText = Fmt.Stars(m.Quality);
        QualityName = strings.Format("Vm_QualityName", Math.Round(m.Quality, 1));
        Reason = m.StoreFolder is { } oldFolder ? strings.Format("Vm_OldFolderRow", oldFolder) : m.Reason ?? "";
        Recommended = m.Recommended;
        Installed = m.Installed;
        Active = m.Active;
        FitsThisPc = m.FitsThisPc;
        ExistingCopy = existingCopy;
        StoreFolder = m.StoreFolder;
        State = m.Installed ? "done" : m.DownloadState ?? "none";
        if (!m.Installed && m.DownloadState is not null && m.SizeBytes > 0)
        {
            Percent = 100.0 * m.BytesDone / m.SizeBytes;
            ProgressText = m.DownloadState == "paused" ? _s.Format("Vm_Paused", (int)Percent) : "";
        }
        Refresh();
    }

    public string Id { get; }
    public string Name { get; }
    public long SizeBytes { get; }
    public string SizeText { get; }

    /// <summary>
    /// "This frees 4.2 GB." from the listing's file size. Empty for a model added in place (the host only takes it
    /// off the list and the file stays), or when the size is not known.
    /// </summary>
    public string FreesText => ExistingCopy || SizeBytes <= 0 ? "" : _s.Format("Vm_RemoveFrees", Fmt.Bytes(SizeBytes));

    /// <summary>Body of the remove confirmation: what is removed, then how much space it gives back.</summary>
    public string RemoveBody => FreesText.Length == 0 ? _s.Format("Vm_RemoveBody", Name) : _s.Format("Vm_RemoveBody", Name) + "\n\n" + FreesText;
    public string QualityText { get; }
    public string QualityName { get; }
    public string Reason { get; }
    public bool Recommended { get; }
    public bool FitsThisPc { get; }
    public bool ExistingCopy { get; }

    /// <summary>Set for a model left in an earlier models folder: it can only be removed.</summary>
    public string? StoreFolder { get; }

    public bool InOldFolder => StoreFolder is not null;
    public string AutoDownload => _s.Format("Vm_AutoDownload", Name);
    public string AutoPause => _s.Format("Vm_AutoPause", Name);
    public string AutoResume => _s.Format("Vm_AutoResume", Name);
    public string AutoCancel => _s.Format("Vm_AutoCancel", Name);
    public string AutoUse => _s.Format("Vm_AutoUse", Name);
    public string AutoRemove => _s.Format("Vm_AutoRemove", Name);

    [ObservableProperty] private bool _installed;
    [ObservableProperty] private bool _active;
    [ObservableProperty] private string _state = "none";
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _badge = "";
    [ObservableProperty] private bool _canDownload;
    [ObservableProperty] private bool _canPause;
    [ObservableProperty] private bool _canResume;
    [ObservableProperty] private bool _canCancel;
    [ObservableProperty] private bool _canActivate;
    [ObservableProperty] private bool _canRemove;
    [ObservableProperty] private bool _showProgress;
    [ObservableProperty] private bool _tooBig;

    public void Apply(DownloadProgressEvent e)
    {
        State = e.State;
        Percent = e.BytesTotal > 0 ? 100.0 * e.BytesDone / e.BytesTotal : 0;
        if (e.State == "done") Installed = true;
        if (e.State == "removed") { Installed = false; State = "none"; Percent = 0; }
        ProgressText = e.State switch
        {
            "downloading" => e.EtaSeconds is { } eta
                ? _s.Format("Vm_DownloadEta", Fmt.Bytes(e.BytesDone), Fmt.Bytes(e.BytesTotal), Math.Max(1, (int)Math.Ceiling(eta / 60)))
                : _s.Format("Vm_DownloadProgress", Fmt.Bytes(e.BytesDone), Fmt.Bytes(e.BytesTotal)),
            "paused" => _s.Format("Vm_Paused", (int)Percent),
            "verifying" => _s.Get("Vm_Verifying"),
            "queued" => _s.Get("Vm_Queued"),
            "failed" => _s.Format("Vm_DownloadFailed", e.Message ?? ""),
            _ => "",
        };
        Refresh();
    }

    public void Refresh()
    {
        var busy = State is "downloading" or "queued" or "verifying";
        ShowProgress = busy || State == "paused";
        CanPause = State == "downloading";
        CanResume = State is "paused" or "failed";
        CanCancel = State is "downloading" or "paused" or "queued";
        CanDownload = !InOldFolder && !Installed && State == "none";
        CanActivate = !InOldFolder && Installed && !Active;
        CanRemove = InOldFolder || (Installed && !Active && !busy);
        if (InOldFolder)
        {
            CanPause = CanResume = CanCancel = ShowProgress = false;
        }

        TooBig = !FitsThisPc && !Installed;
        Badge = InOldFolder ? _s.Get("Vm_OldFolderBadge")
            : Active ? _s.Get("Vm_InUse") : Installed ? _s.Get("Vm_Installed") : Recommended ? _s.Get("Vm_BestForYou") : "";
    }

    partial void OnActiveChanged(bool value) => Refresh();
    partial void OnInstalledChanged(bool value) => Refresh();

    [RelayCommand] private Task DownloadAsync() => _owner.StartAsync(this);
    [RelayCommand] private Task PauseAsync() => _owner.PauseAsync(this);
    [RelayCommand] private Task ResumeAsync() => _owner.ResumeAsync(this);
    [RelayCommand] private Task RemoveAsync() => _owner.RemoveAsync(this);
    [RelayCommand] private Task CancelAsync() => _owner.CancelAsync(this);
    [RelayCommand] private Task ActivateAsync() => _owner.ActivateAsync(this);
}

/// <summary>Models page: what suits this PC, downloads, where files go.</summary>
public sealed partial class ModelsViewModel : ObservableObject, IDisposable
{
    private readonly ShellViewModel _shell;
    private readonly IUiDispatcher _ui;
    private readonly IStrings _s;
    private readonly IDialogService _dialogs;

    public ModelsViewModel(ShellViewModel shell, IUiDispatcher ui, IStrings strings, IDialogService dialogs)
    {
        _shell = shell;
        _ui = ui;
        _s = strings;
        _dialogs = dialogs;
        shell.Host.DownloadProgress += OnProgress;
    }

    public ObservableCollection<ModelRowViewModel> Models { get; } = new();

    [ObservableProperty] private ModelRowViewModel? _recommended;
    [ObservableProperty] private string _storageLine = "";
    [ObservableProperty] private string _existingCopyLine = "";
    [ObservableProperty] private string _upgradeLine = "";
    [ObservableProperty] private string _errorLine = "";
    [ObservableProperty] private bool _hasExisting;
    [ObservableProperty] private bool _hasUpgrade;
    [ObservableProperty] private bool _catalogueUnavailable;

    public async Task LoadAsync()
    {
        try
        {
            var view = await _shell.Host.ListModelsAsync().ConfigureAwait(false);
            _ui.Post(() => Fill(view));
        }
        catch (HostUnavailableException)
        {
            _ui.Post(() => { Models.Clear(); ErrorLine = _s.Get("Vm_NoHostTitle"); });
        }
        catch (HostRequestException ex)
        {
            _ui.Post(() => { Models.Clear(); ErrorLine = ex.Message; });
        }
    }

    private void Fill(ModelCatalogView view)
    {
        ErrorLine = "";
        CatalogueUnavailable = !view.CatalogueAvailable;
        Models.Clear();
        foreach (var m in view.Models)
            Models.Add(new ModelRowViewModel(this, _s, m, view.ExistingCopyIds.Contains(m.Id)));
        Recommended = Models.FirstOrDefault(m => m.Recommended && !m.InOldFolder);
        StorageLine = _s.Format("Vm_StorageLine", view.StorageFolder, Fmt.Bytes(view.FreeBytes));
        var existing = Models.FirstOrDefault(m => m.ExistingCopy);
        ExistingCopyLine = existing is null ? "" : _s.Format("Vm_ExistingCopy", existing.Name);
        var upgrade = view.UpgradeModelId is null ? null : Models.FirstOrDefault(m => m.Id == view.UpgradeModelId);
        UpgradeLine = upgrade is null ? "" : _s.Format("Vm_UpgradeOffer", upgrade.Name);
        HasExisting = ExistingCopyLine.Length > 0;
        HasUpgrade = UpgradeLine.Length > 0;
    }

    private void OnProgress(DownloadProgressEvent e) => _ui.Post(() =>
    {
        var row = Models.FirstOrDefault(m => m.Id == e.CatalogueId && !m.InOldFolder);
        row?.Apply(e);
    });

    public Task StartAsync(ModelRowViewModel row) => Run(() => _shell.Host.StartDownloadAsync(row.Id));
    public Task PauseAsync(ModelRowViewModel row) => Run(() => _shell.Host.PauseDownloadAsync(row.Id));
    public Task ResumeAsync(ModelRowViewModel row) => Run(() => _shell.Host.ResumeDownloadAsync(row.Id));

    public async Task RemoveAsync(ModelRowViewModel row)
    {
        // The host deletes only files it downloaded (store rules); the window only asks, after a confirmation.
        var ok = await _dialogs.ConfirmAsync(_s.Get("Vm_RemoveTitle"), row.RemoveBody, _s.Get("Vm_RemoveYes"), _s.Get("Vm_Cancel"));
        if (!ok) return;
        var removed = row.StoreFolder is { } old
            ? await Run(() => _shell.Host.RemoveOldModelAsync(row.Id, old))
            : await Run(() => _shell.Host.RemoveModelAsync(row.Id));
        if (removed) await LoadAsync();
    }

    public async Task CancelAsync(ModelRowViewModel row)
    {
        // The host removes the partial download it made; the window only asks, after a confirmation.
        var ok = await _dialogs.ConfirmAsync(_s.Get("Vm_CancelDlTitle"), _s.Get("Vm_CancelDlBody"), _s.Get("Vm_CancelDlYes"), _s.Get("Vm_CancelDlKeep"));
        if (!ok) return;
        await Run(() => _shell.Host.CancelDownloadAsync(row.Id));
        _ui.Post(() => { row.State = "none"; row.Percent = 0; row.ProgressText = ""; row.Refresh(); });
    }

    public async Task ActivateAsync(ModelRowViewModel row)
    {
        await Run(() => _shell.Host.ActivateModelAsync(row.Id));
        await LoadAsync();
    }

    private async Task<bool> Run(Func<Task> call)
    {
        try
        {
            await call().ConfigureAwait(false);
            _ui.Post(() => ErrorLine = "");
            return true;
        }
        catch (HostUnavailableException)
        {
            _ui.Post(() => ErrorLine = _s.Get("Vm_NoHostTitle"));
        }
        catch (HostRequestException ex)
        {
            _ui.Post(() => ErrorLine = ex.Message);
        }
        return false;
    }

    public void Dispose() => _shell.Host.DownloadProgress -= OnProgress;
}
