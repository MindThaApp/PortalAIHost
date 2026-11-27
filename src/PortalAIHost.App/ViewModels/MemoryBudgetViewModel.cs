using CommunityToolkit.Mvvm.ComponentModel;
using Portal.AiHost.Contracts;
using PortalAIHost.App.Services;

namespace PortalAIHost.App.ViewModels;

/// <summary>One row of the budget bar: used, reserved (loading) and free parts of a limit.</summary>
public sealed partial class BudgetBarViewModel : ObservableObject
{
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private double _usedPercent;
    [ObservableProperty] private double _reservedEndPercent;
    [ObservableProperty] private bool _visible;

    public void Set(string line, long limit, long used, long reserved)
    {
        Text = line;
        var total = Math.Max(1.0, limit);
        UsedPercent = Math.Clamp(used * 100.0 / total, 0, 100);
        ReservedEndPercent = Math.Clamp((used + reserved) * 100.0 / total, UsedPercent, 100);
        Visible = limit > 0;
    }
}

/// <summary>
/// The small RAM and graphics-memory bars on the Models page, from /host/status memory. Nothing is asked of the host
/// here: it reads the snapshot the shell already has, so the page never waits. Until data arrives the bars keep
/// their place and show a placeholder.
/// </summary>
public sealed partial class MemoryBudgetViewModel : ObservableObject, IDisposable
{
    private readonly ShellViewModel _shell;
    private readonly IUiDispatcher _ui;
    private readonly IStrings _s;

    public MemoryBudgetViewModel(ShellViewModel shell, IUiDispatcher ui, IStrings strings)
    {
        _shell = shell;
        _ui = ui;
        _s = strings;
        shell.SnapshotChanged += OnChanged;
        Apply(shell.Snapshot.Status?.Memory);
    }

    public BudgetBarViewModel Ram { get; } = new();

    public BudgetBarViewModel Vram { get; } = new();

    /// <summary>False until the host reported memory; the bars fade in without moving the layout.</summary>
    [ObservableProperty] private bool _hasData;

    public double BarOpacity => HasData ? 1 : 0;

    public double PlaceholderOpacity => HasData ? 0 : 1;

    partial void OnHasDataChanged(bool value)
    {
        OnPropertyChanged(nameof(BarOpacity));
        OnPropertyChanged(nameof(PlaceholderOpacity));
    }

    private void OnChanged() => _ui.Post(() => Apply(_shell.Snapshot.Status?.Memory));

    internal void Apply(MemoryBudgetStatus? m)
    {
        if (m is null)
        {
            HasData = false;
            return;
        }

        long Loading(Func<EngineMemoryStatus, long> pick) => m.Engines.Where(e => e.Loading).Sum(pick);
        var ramReserved = Loading(e => e.RamBytes);
        var ramUsed = Math.Max(0, m.RamPlannedBytes - ramReserved);
        Ram.Set(_s.Format("Vm_MemLine", _s.Get("Vm_MemRam"), Fmt.Bytes(ramUsed), Fmt.Bytes(ramReserved),
            Fmt.Bytes(m.RamFreeBytes), Fmt.Bytes(m.RamLimitBytes)), m.RamLimitBytes, ramUsed, ramReserved);

        var vramReserved = Loading(e => e.VramBytes);
        var vramUsed = Math.Max(0, m.VramPlannedBytes - vramReserved);
        Vram.Set(_s.Format("Vm_MemLine", _s.Get("Vm_MemVram"), Fmt.Bytes(vramUsed), Fmt.Bytes(vramReserved),
            Fmt.Bytes(m.VramFreeBytes), Fmt.Bytes(m.VramTotalBytes)), m.VramTotalBytes, vramUsed, vramReserved);
        HasData = true;
    }

    public void Dispose() => _shell.SnapshotChanged -= OnChanged;
}
