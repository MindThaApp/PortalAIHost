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
        Kind = m.Kind;
        Category = ModelCategories.Resolve(m.Category, m.Kind);
        ComingSoon = m.ComingSoon;
        VramText = VramLine(strings, m);
        Name = m.DisplayName;
        SizeBytes = m.SizeBytes;
        SizeText = Fmt.Bytes(m.SizeBytes);
        var stars = Fmt.StarScale(m.Quality);
        QualityScore = m.Quality;
        QualityText = Fmt.Stars(stars);
        QualityName = strings.Format("Vm_QualityName", Math.Round(stars, 1));
        Reason = m.StoreFolder is { } oldFolder ? strings.Format("Vm_OldFolderRow", Fmt.PrivatePath(oldFolder))
            : m.UserAdded ? strings.Get("Vm_UserAddedReason") : m.Reason ?? "";
        Recommended = m.Recommended;
        Installed = m.Installed;
        Active = m.Active;
        FitsThisPc = m.FitsThisPc;
        ExistingCopy = existingCopy;
        UserAdded = m.UserAdded;
        StoreFolder = m.StoreFolder;
        State = m.Installed ? "done" : m.DownloadState ?? "none";
        if (!m.Installed && m.DownloadState is not null && m.SizeBytes > 0)
        {
            Percent = 100.0 * m.BytesDone / m.SizeBytes;
            ProgressText = m.DownloadState == "paused" ? _s.Format("Vm_Paused", (int)Percent) : "";
        }

        LastUsedText = !m.Installed || m.StoreFolder is not null ? ""
            : m.LastUsedAt is { } t ? AgoText(t, _owner.Now()) : _s.Get("Vm_NeverUsed");
        Damaged = m.Installed && m.Damaged;
        if (Damaged) CheckText = _s.Format("Vm_CheckDamaged", m.DamagedReason ?? "");
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(m.AdviceSummary)) details.Add(m.AdviceSummary);
        if (m.GoodFor is { Count: > 0 }) details.Add(_s.Format("Vm_GoodFor", string.Join(", ", m.GoodFor)));
        if (m.Caveats is { Count: > 0 }) details.Add(_s.Format("Vm_Caveats", string.Join(", ", m.Caveats)));
        AdviceText = string.Join("\n", details);
        MemoryText = m.MemoryNeededBytes > 0 ? _s.Format("Vm_MemoryNeeded", Fmt.Bytes(m.MemoryNeededBytes)) : "";
        LicenceName = m.LicenceName ?? "";
        LicenceUri = Uri.TryCreate(m.LicenceUrl, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? u : null;
        Refresh();
    }

    public string Id { get; }
    public string Kind { get; }

    /// <summary>Use-case category id (from the model list, or derived from the kind).</summary>
    public string Category { get; }

    /// <summary>The host has no engine for this model yet: shown, never downloadable.</summary>
    public bool ComingSoon { get; }

    /// <summary>"Needs 8 GB graphics memory with CPU offload; 12 GB recommended". Empty when the list gives none.</summary>
    public string VramText { get; }
    public bool HasVram => VramText.Length > 0;

    private static string VramLine(IStrings s, CatalogueModelSummary m) =>
        m.MinVramGb is { } min && m.RecommendedVramGb is { } rec ? s.Format("Vm_VramBoth", min, rec)
        : m.MinVramGb is { } only ? s.Format("Vm_VramMin", only)
        : m.RecommendedVramGb is { } r ? s.Format("Vm_VramRec", r) : "";
    public string Name { get; }
    public long SizeBytes { get; }
    public string SizeText { get; }
    public double QualityScore { get; }

    /// <summary>"Last used 3 days ago" or "Not used yet". Empty for models that are not installed.</summary>
    public string LastUsedText { get; }

    /// <summary>What the model is good at, from the model list's advice. Empty when the list has none.</summary>
    public string AdviceText { get; }
    public bool HasAdvice => AdviceText.Length > 0;

    /// <summary>Memory the planner says the model needs on this PC. Empty when unknown.</summary>
    public string MemoryText { get; }
    public bool HasMemory => MemoryText.Length > 0;
    public string LicenceName { get; }
    public Uri? LicenceUri { get; }
    public bool HasLicence => LicenceName.Length > 0;

    /// <summary>The licence name shown as the link text.</summary>
    public string LicenceText => _s.Format("Vm_Licence", LicenceName);

    /// <summary>"This frees 4.2 GB." from the listing's file size. Empty for a model added in place or when unknown.</summary>
    public string FreesText => ExistingCopy || SizeBytes <= 0 ? "" : _s.Format("Vm_RemoveFrees", Fmt.Bytes(SizeBytes));

    /// <summary>Body of the remove confirmation: what is removed (and that the model in use is unloaded first), then the space it frees.</summary>
    public string RemoveBody
    {
        get
        {
            var body = _s.Format(UserAdded ? "Vm_RemoveUserBody" : Active ? "Vm_RemoveActiveBody" : "Vm_RemoveBody", Name);
            return FreesText.Length == 0 ? body : body + "\n\n" + FreesText;
        }
    }

    /// <summary>The confirm button names what happens: "Remove Name (4.2 GB)".</summary>
    public string RemoveVerb => FreesText.Length == 0 ? _s.Format("Vm_RemoveVerbNoSize", Name) : _s.Format("Vm_RemoveVerb", Name, SizeText);

    public string QualityText { get; }
    public string QualityName { get; }
    public string Reason { get; }
    public bool Recommended { get; }
    public bool FitsThisPc { get; }
    public bool ExistingCopy { get; }

    /// <summary>A model file the user added ("Add your own model"): the host holds its own copy; fit is not estimated.</summary>
    public bool UserAdded { get; }

    /// <summary>Set for a model left in an earlier models folder: it can only be removed.</summary>
    public string? StoreFolder { get; }

    public bool InOldFolder => StoreFolder is not null;
    public string AutoDownload => _s.Format("Vm_AutoDownload", Name);
    public string AutoPause => _s.Format("Vm_AutoPause", Name);
    public string AutoResume => _s.Format("Vm_AutoResume", Name);
    public string AutoCancel => _s.Format("Vm_AutoCancel", Name);
    public string AutoUse => _s.Format("Vm_AutoUse", Name);
    public string AutoRemove => _s.Format("Vm_AutoRemove", Name);
    public string AutoCheck => _s.Format("Vm_AutoCheck", Name);
    public string AutoRedownload => _s.Format("Vm_AutoRedownload", Name);

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

    /// <summary>True from the click until the host answers: the row says "Removing..." at once.</summary>
    [ObservableProperty] private bool _removing;
    [ObservableProperty] private bool _checking;
    [ObservableProperty] private double _checkPercent;
    [ObservableProperty] private string _checkText = "";
    [ObservableProperty] private bool _damaged;
    [ObservableProperty] private bool _canCheck;
    [ObservableProperty] private bool _canRedownload;

    public bool HasCheckText => CheckText.Length > 0;

    public void Apply(DownloadProgressEvent e)
    {
        if (e.State == "removing") { Removing = true; Refresh(); return; }
        if (e.State == "remove_failed") { Removing = false; Refresh(); return; }
        State = e.State;
        Percent = e.BytesTotal > 0 ? 100.0 * e.BytesDone / e.BytesTotal : 0;
        if (e.State == "done") Installed = true;
        if (e.State == "removed") { Installed = false; State = "none"; Percent = 0; Removing = false; }
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

    /// <summary>Shows the progress or result of a file check.</summary>
    public void ApplyCheck(StoreProgressEvent e)
    {
        switch (e.State)
        {
            case StoreStates.Running:
                Checking = true;
                CheckPercent = 100 * e.Fraction;
                CheckText = _s.Format("Vm_Checking", (int)CheckPercent);
                break;
            case StoreStates.Done:
                Checking = false;
                Damaged = false;
                CheckPercent = 100;
                CheckText = _s.Get("Vm_CheckOk");
                break;
            case StoreStates.Damaged:
                Checking = false;
                Damaged = true;
                CheckText = _s.Format("Vm_CheckDamaged", e.Message ?? "");
                break;
            case StoreStates.Failed:
                Checking = false;
                CheckText = _s.Format("Vm_CheckFailed", e.Message ?? "");
                break;
            default: // cancelled
                Checking = false;
                CheckText = Damaged ? CheckText : "";
                break;
        }

        Refresh();
    }

    public void Refresh()
    {
        var busy = State is "downloading" or "queued" or "verifying";
        ShowProgress = busy || State == "paused";
        CanPause = State == "downloading";
        CanResume = State is "paused" or "failed";
        CanCancel = State is "downloading" or "paused" or "queued";
        CanDownload = !InOldFolder && !Installed && !ComingSoon && State == "none";
        CanActivate = !InOldFolder && Installed && !Active && !Removing && !Damaged;
        // The model in use can be removed too: the host unloads it first.
        CanRemove = InOldFolder || (Installed && !busy && !Removing && !Checking);
        CanCheck = !InOldFolder && Installed && !ExistingCopy && !UserAdded && !Checking && !Removing;
        CanRedownload = !InOldFolder && Installed && Damaged && !Checking && !Removing && !ExistingCopy && !UserAdded;
        if (InOldFolder)
        {
            CanPause = CanResume = CanCancel = ShowProgress = false;
        }

        if (Removing)
        {
            ShowProgress = true;
            ProgressText = _s.Get("Vm_Removing");
            CanPause = CanResume = CanCancel = CanDownload = false;
        }

        TooBig = !FitsThisPc && !Installed && !ComingSoon;
        Badge = InOldFolder ? _s.Get("Vm_OldFolderBadge")
            : Removing ? _s.Get("Vm_RemovingBadge")
            : Damaged ? _s.Get("Vm_DamagedBadge")
            : Active ? _s.Get("Vm_InUse") : Installed ? _s.Get(UserAdded ? "Vm_UserAddedBadge" : "Vm_Installed") : ComingSoon && !Installed ? _s.Get("Vm_ComingSoon") : Recommended ? _s.Get("Vm_BestForYou") : "";
    }

    partial void OnActiveChanged(bool value) => Refresh();
    partial void OnInstalledChanged(bool value) => Refresh();
    partial void OnRemovingChanged(bool value) => Refresh();
    partial void OnCheckingChanged(bool value) => Refresh();
    partial void OnCheckTextChanged(string value) => OnPropertyChanged(nameof(HasCheckText));

    /// <summary>"today", "yesterday" or "N days ago" style text.</summary>
    internal string AgoText(DateTimeOffset when, DateTimeOffset now)
    {
        var days = (int)Math.Floor((now - when).TotalDays);
        return days switch
        {
            <= 0 => _s.Get("Vm_UsedToday"),
            1 => _s.Get("Vm_UsedYesterday"),
            < 60 => _s.Format("Vm_UsedDays", days),
            _ => _s.Format("Vm_UsedMonths", days / 30),
        };
    }

    [RelayCommand] private Task DownloadAsync() => _owner.StartAsync(this);
    [RelayCommand] private Task PauseAsync() => _owner.PauseAsync(this);
    [RelayCommand] private Task ResumeAsync() => _owner.ResumeAsync(this);
    [RelayCommand] private Task RemoveAsync() => _owner.RemoveAsync(this);
    [RelayCommand] private Task CancelAsync() => _owner.CancelAsync(this);
    [RelayCommand] private Task ActivateAsync() => _owner.ActivateAsync(this);
    [RelayCommand] private Task CheckAsync() => _owner.CheckAsync(this);
    [RelayCommand] private Task CancelCheckAsync() => _owner.CancelStoreOperationAsync();
    [RelayCommand] private Task RedownloadAsync() => _owner.RedownloadAsync(this);
}

/// <summary>Models page: what suits this PC, downloads, where files go.</summary>
public sealed partial class ModelsViewModel : ObservableObject, IDisposable
{
    private readonly ShellViewModel _shell;
    private readonly IUiDispatcher _ui;
    private readonly IStrings _s;
    private readonly IDialogService _dialogs;
    private readonly IFolderOpener? _folders;
    private readonly IModelFilePicker? _picker;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>Models that were in use when "Download again" removed them: chosen again when the download is done.</summary>
    private readonly HashSet<string> _reactivate = new(StringComparer.Ordinal);

    public ModelsViewModel(ShellViewModel shell, IUiDispatcher ui, IStrings strings, IDialogService dialogs,
        IFolderOpener? folders = null, Func<DateTimeOffset>? clock = null, IModelFilePicker? picker = null)
    {
        _shell = shell;
        _ui = ui;
        _s = strings;
        _dialogs = dialogs;
        _folders = folders;
        _picker = picker;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        shell.Host.DownloadProgress += OnProgress;
        shell.Host.StoreProgress += OnStoreProgress;
    }

    internal DateTimeOffset Now() => _clock();

    /// <summary>Every row (downloaded, catalogue and earlier folders).</summary>
    public ObservableCollection<ModelRowViewModel> Models { get; } = new();

    /// <summary>Installed models in the current models folder, shown at the top.</summary>
    public ObservableCollection<ModelRowViewModel> Downloaded { get; } = new();

    /// <summary>Everything else: models to download and models left in earlier folders.</summary>
    public ObservableCollection<ModelRowViewModel> Catalogue { get; } = new();

    [ObservableProperty] private ModelRowViewModel? _recommended;
    [ObservableProperty] private string _storageLine = "";
    [ObservableProperty] private string _existingCopyLine = "";
    [ObservableProperty] private string _upgradeLine = "";
    [ObservableProperty] private string _errorLine = "";
    [ObservableProperty] private bool _hasExisting;
    [ObservableProperty] private bool _hasUpgrade;
    [ObservableProperty] private bool _catalogueUnavailable;
    [ObservableProperty] private bool _hasDownloaded;
    [ObservableProperty] private bool _canOpenFolder;

    /// <summary>Why the last removal failed; the row was put back as it was.</summary>
    [ObservableProperty] private string _removeFailure = "";
    [ObservableProperty] private bool _hasRemoveFailure;

    /// <summary>A move of the models folder is running (or just ended): progress or result text.</summary>
    [ObservableProperty] private string _storeBusyLine = "";
    [ObservableProperty] private bool _storeBusy;
    [ObservableProperty] private double _storeBusyPercent;

    public async Task LoadAsync()
    {
        try
        {
            var view = await _shell.Host.ListModelsAsync().ConfigureAwait(false);
            _ui.Post(() => Fill(view));
        }
        catch (HostUnavailableException)
        {
            _ui.Post(() => { Clear(); ErrorLine = _s.Get("Vm_NoHostTitle"); });
        }
        catch (HostRequestException ex)
        {
            _ui.Post(() => { Clear(); ErrorLine = ex.Message; });
        }
    }

    private void Clear()
    {
        Models.Clear();
        Downloaded.Clear();
        Catalogue.Clear();
        _ = RegroupAsync();
    }

    private void Fill(ModelCatalogView view)
    {
        ErrorLine = "";
        CatalogueUnavailable = !view.CatalogueAvailable;
        Clear();
        foreach (var m in view.Models)
        {
            var row = new ModelRowViewModel(this, _s, m, view.ExistingCopyIds.Contains(m.Id));
            Models.Add(row);
            if (row.Installed && !row.InOldFolder) Downloaded.Add(row); else Catalogue.Add(row);
        }

        HasDownloaded = Downloaded.Count > 0;
        _ = RegroupAsync();
        CanOpenFolder = view.StorageFolder.Length > 0;
        Recommended = Models.FirstOrDefault(m => m.Recommended && !m.InOldFolder);
        StorageLine = _s.Format("Vm_StorageLine", Fmt.PrivatePath(view.StorageFolder), Fmt.Bytes(view.FreeBytes), Fmt.Bytes(view.ModelsBytes));
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
        // A finished download or a removal moves the row between the two lists.
        if (e.State == "done" && _reactivate.Remove(e.CatalogueId))
        {
            _ = ReactivateAsync(e.CatalogueId);
        }
        else if (e.State is "done" or "removed")
        {
            _ = LoadAsync();
        }
    });

    private async Task ReactivateAsync(string id)
    {
        await Run(() => _shell.Host.ActivateModelAsync(id));
        await LoadAsync();
    }

    private void OnStoreProgress(StoreProgressEvent e) => _ui.Post(() =>
    {
        if (e.Operation == StoreOperations.Cleanup)
        {
            if (e.State == StoreStates.Running && CleanupWorking && e.Message is { Length: > 0 } note) CleanupLine = _s.Get("Vm_CleanupRunning") + " " + note;
            return;
        }

        if (e.Operation == StoreOperations.Verify)
        {
            Models.FirstOrDefault(m => m.Id == e.CatalogueId && !m.InOldFolder)?.ApplyCheck(e);
            return;
        }

        if (e.Operation == StoreOperations.Import)
        {
            StoreBusy = e.State == StoreStates.Running;
            StoreBusyPercent = 100 * e.Fraction;
            StoreBusyLine = e.State switch
            {
                StoreStates.Running => _s.Format("Vm_ImportRunning", (int)StoreBusyPercent),
                StoreStates.Done => e.Message ?? _s.Get("Vm_ImportDone"),
                StoreStates.Cancelled => _s.Get("Vm_ImportCancelled"),
                _ => _s.Format("Vm_ImportFailed", e.Message ?? ""),
            };
            if (!StoreBusy) _ = LoadAsync();
            return;
        }

        StoreBusy = e.State is StoreStates.Running or StoreStates.RemovingOld;
        StoreBusyPercent = 100 * e.Fraction;
        StoreBusyLine = e.State switch
        {
            StoreStates.Running => _s.Format("Vm_MoveRunning", (int)StoreBusyPercent),
            StoreStates.RemovingOld => _s.Get("Vm_MoveRemovingOld"),
            StoreStates.Done => e.Message ?? _s.Get("Vm_MoveDone"),
            StoreStates.Cancelled => _s.Get("Vm_MoveCancelled"),
            _ => _s.Format("Vm_MoveFailed", e.Message ?? ""),
        };
        if (!StoreBusy) _ = LoadAsync();
    });

    public Task StartAsync(ModelRowViewModel row) => Run(() => _shell.Host.StartDownloadAsync(row.Id));
    public Task PauseAsync(ModelRowViewModel row) => Run(() => _shell.Host.PauseDownloadAsync(row.Id));
    public Task ResumeAsync(ModelRowViewModel row) => Run(() => _shell.Host.ResumeDownloadAsync(row.Id));

    /// <summary>
    /// Asks, then has the host remove the model. The model in use can go too: the host unloads it first and, when
    /// another installed model of the same kind exists, the user may switch to it. The row says "Removing..." at
    /// once; a refusal puts it back and shows the reason in a bar.
    /// </summary>
    public async Task RemoveAsync(ModelRowViewModel row)
    {
        // The host deletes only files it downloaded (store rules); the window only asks, after a confirmation.
        var ok = await _dialogs.ConfirmAsync(_s.Get("Vm_RemoveTitle"), row.RemoveBody, row.RemoveVerb, _s.Get("Vm_Cancel"));
        if (!ok) return;
        string? switchTo = null;
        if (row.Active && !row.InOldFolder && AlternativeFor(row) is { } other)
        {
            var switchAnswer = await _dialogs.ConfirmAsync(_s.Get("Vm_SwitchTitle"), _s.Format("Vm_SwitchBody", row.Name, other.Name),
                _s.Format("Vm_SwitchYes", other.Name), _s.Get("Vm_SwitchNo"));
            if (switchAnswer) switchTo = other.Id;
        }

        await RemoveRowAsync(row, switchTo, damaged: false);
    }

    private async Task<bool> RemoveRowAsync(ModelRowViewModel row, string? switchTo, bool damaged)
    {
        HasRemoveFailure = false;
        RemoveFailure = "";
        row.Removing = true; // same frame: "Removing..."
        var error = row.StoreFolder is { } old
            ? await TryAsync(() => _shell.Host.RemoveOldModelAsync(row.Id, old))
            : damaged ? await TryAsync(() => _shell.Host.RemoveDamagedModelAsync(row.Id))
            : await TryAsync(() => _shell.Host.RemoveModelAsync(row.Id, switchTo));
        if (error is not null)
        {
            _ui.Post(() =>
            {
                row.Removing = false; // roll back
                RemoveFailure = _s.Format("Vm_RemoveFailed", row.Name, error);
                HasRemoveFailure = true;
            });
            return false;
        }

        _ui.Post(() => ErrorLine = "");
        await LoadAsync();
        return true;
    }

    /// <summary>Another installed model of the same kind to offer as the replacement, best first.</summary>
    private ModelRowViewModel? AlternativeFor(ModelRowViewModel row) =>
        Models.Where(m => m.Installed && !m.InOldFolder && m.Id != row.Id && m.Kind == row.Kind && !m.Damaged)
            .OrderByDescending(m => m.Recommended).ThenByDescending(m => m.QualityScore).FirstOrDefault();

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

    /// <summary>Has the host re-hash the model against the model list. Progress and the result arrive as events.</summary>
    public async Task CheckAsync(ModelRowViewModel row)
    {
        row.Checking = true;
        row.CheckPercent = 0;
        row.CheckText = _s.Format("Vm_Checking", 0);
        var error = await TryAsync(() => _shell.Host.VerifyModelAsync(row.Id));
        if (error is not null)
        {
            _ui.Post(() =>
            {
                row.Checking = false;
                row.CheckText = _s.Format("Vm_CheckFailed", error);
            });
        }
    }

    /// <summary>
    /// For a damaged model: after a confirmation, removes the damaged copy through the store and downloads it again.
    /// The check itself deletes nothing.
    /// </summary>
    public async Task RedownloadAsync(ModelRowViewModel row)
    {
        var ok = await _dialogs.ConfirmAsync(_s.Get("Vm_RedownloadTitle"),
            _s.Format(row.Active ? "Vm_RedownloadActiveBody" : "Vm_RedownloadBody", row.Name),
            _s.Format("Vm_RedownloadYes", row.Name), _s.Get("Vm_Cancel"));
        if (!ok) return;
        var wasActive = row.Active;
        if (!await RemoveRowAsync(row, null, damaged: true)) return;
        if (wasActive) _reactivate.Add(row.Id);
        if (!await Run(() => _shell.Host.StartDownloadAsync(row.Id))) _reactivate.Remove(row.Id);
    }

    public Task CancelStoreOperationAsync() => Run(() => _shell.Host.CancelStoreOperationAsync());

    /// <summary>Opens the models folder in Explorer. The host only says where it is.</summary>
    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        try
        {
            var path = await _shell.Host.GetStoreFolderAsync().ConfigureAwait(false);
            _folders?.Open(path);
            _ui.Post(() => ErrorLine = "");
        }
        catch (HostUnavailableException)
        {
            _ui.Post(() => ErrorLine = _s.Get("Vm_NoHostTitle"));
        }
        catch (Exception ex) when (ex is HostRequestException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _ui.Post(() => ErrorLine = ex.Message);
        }
    }

    /// <summary>
    /// "Add your own model": the only entry point. Asks for a .gguf file, then has the host copy it into its models
    /// folder; progress and the result arrive as events. Pro gating, when it comes, goes at the top of this method.
    /// </summary>
    [RelayCommand]
    private async Task AddOwnModelAsync()
    {
        if (_picker is null || StoreBusy) return;
        var path = await _picker.PickModelFileAsync();
        if (string.IsNullOrEmpty(path)) return;
        _ui.Post(() => { StoreBusy = true; StoreBusyPercent = 0; StoreBusyLine = _s.Format("Vm_ImportRunning", 0); });
        var error = await TryAsync(() => _shell.Host.ImportModelAsync(path));
        if (error is not null)
        {
            _ui.Post(() => { StoreBusy = false; StoreBusyLine = _s.Format("Vm_ImportFailed", error); });
        }
    }

    [RelayCommand]
    private Task CancelMoveAsync() => CancelStoreOperationAsync();

    /// <summary>Runs a host call; returns null on success, otherwise the plain-language reason.</summary>
    private async Task<string?> TryAsync(Func<Task> call)
    {
        try
        {
            await call().ConfigureAwait(false);
            return null;
        }
        catch (HostUnavailableException)
        {
            return _s.Get("Vm_NoHostTitle");
        }
        catch (HostRequestException ex)
        {
            return ex.Message;
        }
    }

    private async Task<bool> Run(Func<Task> call)
    {
        var error = await TryAsync(call);
        _ui.Post(() => ErrorLine = error ?? "");
        return error is null;
    }

    public void Dispose()
    {
        _shell.Host.DownloadProgress -= OnProgress;
        _shell.Host.StoreProgress -= OnStoreProgress;
    }
}
