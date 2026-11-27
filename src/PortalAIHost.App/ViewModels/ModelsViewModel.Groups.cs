using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Portal.AiHost.Contracts;
using PortalAIHost.App.Services;

namespace PortalAIHost.App.ViewModels;

/// <summary>One filter chip: a category (or "All").</summary>
public sealed partial class ModelCategoryChip : ObservableObject
{
    private readonly ModelsViewModel _owner;

    public ModelCategoryChip(ModelsViewModel owner, string id, string label, string glyph, string tooltip)
    {
        _owner = owner;
        Id = id;
        Label = label;
        Glyph = glyph;
        Tooltip = tooltip;
    }

    /// <summary>Category id, or <see cref="ModelGrouping.All"/> for the "All" chip.</summary>
    public string Id { get; }
    public string Label { get; }
    public string Glyph { get; }
    public string Tooltip { get; }

    [ObservableProperty] private bool _selected;

    partial void OnSelectedChanged(bool value) => _owner.OnChipToggled(this, value);
}

/// <summary>One category on the page: its title and the rows shown (the top few until expanded).</summary>
public sealed partial class ModelGroupViewModel : ObservableObject
{
    private readonly ModelsViewModel _owner;
    private readonly IStrings _s;

    public ModelGroupViewModel(ModelsViewModel owner, IStrings strings, string categoryId)
    {
        _owner = owner;
        _s = strings;
        CategoryId = categoryId;
        Title = strings.Get("Vm_Cat_" + categoryId);
        Glyph = ModelsViewModel.GlyphFor(categoryId);
    }

    public string CategoryId { get; }
    public string Title { get; }
    public string Glyph { get; }
    public ObservableCollection<ModelRowViewModel> Rows { get; } = new();

    [ObservableProperty] private int _total;
    [ObservableProperty] private bool _expanded;

    /// <summary>True when "Show all" has something to reveal (or "Show fewer" has something to hide).</summary>
    [ObservableProperty] private bool _canToggle;
    [ObservableProperty] private string _toggleText = "";
    [ObservableProperty] private string _countText = "";

    internal void Update(int total, bool expanded, bool canToggle)
    {
        Total = total;
        Expanded = expanded;
        CanToggle = canToggle;
        CountText = _s.Format("Vm_GroupCount", total);
        ToggleText = expanded ? _s.Get("Vm_ShowFewer") : _s.Format("Vm_ShowAll", total);
    }

    [RelayCommand]
    private void ToggleAll() => _owner.ToggleGroup(CategoryId);
}

public sealed partial class ModelsViewModel
{
    /// <summary>Above this many models the plan is computed on a background thread; below it, in place (no flash).</summary>
    internal const int OffThreadThreshold = 40;

    private readonly Dictionary<string, ModelGroupViewModel> _groupCache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expandedGroups = new(StringComparer.Ordinal);
    private ModelCategoryChip? _allChip;
    private bool _settingChips;
    private int _regroupVersion;
    private CancellationTokenSource? _regroupCts;

    /// <summary>Filter chips: "All" first, then the categories that have models.</summary>
    public ObservableCollection<ModelCategoryChip> Chips { get; } = new();

    /// <summary>Models to add, grouped by category.</summary>
    public ObservableCollection<ModelGroupViewModel> Groups { get; } = new();

    /// <summary>Installed models that match the filter and search (the Downloaded section).</summary>
    public ObservableCollection<ModelRowViewModel> DownloadedShown { get; } = new();

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _categoryFilter = ModelGrouping.All;
    [ObservableProperty] private bool _hasDownloadedShown;
    [ObservableProperty] private bool _noMatches;

    partial void OnSearchTextChanged(string value) => _ = RegroupAsync(debounce: true);

    partial void OnCategoryFilterChanged(string value)
    {
        _settingChips = true;
        foreach (var c in Chips) c.Selected = c.Id == value;
        _settingChips = false;
        _ = RegroupAsync();
    }

    internal void OnChipToggled(ModelCategoryChip chip, bool selected)
    {
        if (_settingChips) return;
        if (selected)
        {
            CategoryFilter = chip.Id;
        }
        else if (CategoryFilter == chip.Id)
        {
            // A chip stays selected when clicked again: one category (or All) is always chosen.
            _settingChips = true;
            chip.Selected = true;
            _settingChips = false;
        }
    }

    internal void ToggleGroup(string categoryId)
    {
        if (!_expandedGroups.Remove(categoryId)) _expandedGroups.Add(categoryId);
        _ = RegroupAsync();
    }

    internal static string GlyphFor(string categoryId) => categoryId switch
    {
        ModelCategories.Chat => "",
        ModelCategories.Coding => "",
        ModelCategories.Reasoning => "",
        ModelCategories.Vision => "",
        ModelCategories.Search => "",
        ModelCategories.Image => "",
        ModelCategories.Video => "",
        ModelCategories.Speech => "",
        ModelCategories.Other => "",
        _ => "", // All
    };

    private static IReadOnlyList<GroupInput> Inputs(IEnumerable<ModelRowViewModel> rows) =>
        rows.Select(r => new GroupInput(r.Id, r.Name, r.Category, r.Recommended, r.QualityScore, r.ComingSoon)).ToList();

    /// <summary>
    /// Rebuilds the chips, the groups and the Downloaded list from the current rows, search text and filter. Small
    /// lists are planned in place; large ones on a background thread (with a short pause while the person types), and
    /// only the last request is applied. Existing group objects and collections are updated, not replaced, so the
    /// layout does not jump.
    /// </summary>
    public Task RegroupAsync(bool debounce = false)
    {
        var version = Interlocked.Increment(ref _regroupVersion);
        _regroupCts?.Cancel();
        var catalogueRows = Catalogue.Where(r => !r.InOldFolder).ToList();
        var inputs = Inputs(catalogueRows);
        var downloaded = Downloaded.ToList();
        var downloadedInputs = Inputs(downloaded);
        var search = SearchText;
        var filter = CategoryFilter;
        var expanded = _expandedGroups.ToHashSet(StringComparer.Ordinal);

        if (inputs.Count + downloadedInputs.Count <= OffThreadThreshold)
        {
            var plan = ModelGrouping.Plan(inputs, filter, search, expanded);
            Apply(version, catalogueRows, downloaded, downloadedInputs, inputs, plan, filter, search);
            return Task.CompletedTask;
        }

        var cts = _regroupCts = new CancellationTokenSource();
        return PlanOffThreadAsync(version, debounce, cts.Token, catalogueRows, downloaded, downloadedInputs, inputs, filter, search, expanded);
    }

    private async Task PlanOffThreadAsync(int version, bool debounce, CancellationToken ct,
        List<ModelRowViewModel> catalogueRows, List<ModelRowViewModel> downloaded,
        IReadOnlyList<GroupInput> downloadedInputs, IReadOnlyList<GroupInput> inputs,
        string filter, string search, IReadOnlySet<string> expanded)
    {
        try
        {
            if (debounce) await Task.Delay(150, ct).ConfigureAwait(false);
            var plan = await Task.Run(() => ModelGrouping.Plan(inputs, filter, search, expanded), ct).ConfigureAwait(false);
            _ui.Post(() => Apply(version, catalogueRows, downloaded, downloadedInputs, inputs, plan, filter, search));
        }
        catch (OperationCanceledException)
        {
            // A newer request replaced this one.
        }
    }

    private void Apply(int version, List<ModelRowViewModel> catalogueRows, List<ModelRowViewModel> downloaded,
        IReadOnlyList<GroupInput> downloadedInputs, IReadOnlyList<GroupInput> inputs, IReadOnlyList<GroupPlan> plan, string filter, string search)
    {
        if (version != Volatile.Read(ref _regroupVersion)) return;

        SyncChips(inputs);
        var byId = catalogueRows.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var groups = new List<ModelGroupViewModel>();
        var autoExpanded = !string.IsNullOrWhiteSpace(search) || !string.IsNullOrEmpty(filter);
        foreach (var p in plan)
        {
            if (!_groupCache.TryGetValue(p.Category, out var g))
            {
                g = _groupCache[p.Category] = new ModelGroupViewModel(this, _s, p.Category);
            }

            var isExpanded = autoExpanded || _expandedGroups.Contains(p.Category);
            SyncList(g.Rows, p.VisibleIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList());
            g.Update(p.Total, isExpanded, canToggle: !autoExpanded && (p.Total > ModelGrouping.CollapsedCount || _expandedGroups.Contains(p.Category)));
            groups.Add(g);
        }

        SyncList(Groups, groups);

        var shown = downloadedInputs.Where(r => (string.IsNullOrEmpty(filter) || r.Category == filter) && ModelGrouping.Matches(r, search))
            .Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        SyncList(DownloadedShown, downloaded.Where(r => shown.Contains(r.Id)).ToList());
        HasDownloadedShown = DownloadedShown.Count > 0;
        NoMatches = Groups.Count == 0 && DownloadedShown.Count == 0 && (catalogueRows.Count + downloaded.Count) > 0;
    }

    private void SyncChips(IReadOnlyList<GroupInput> inputs)
    {
        var counts = ModelGrouping.Counts(inputs);
        var wanted = new List<ModelCategoryChip>();
        _allChip ??= new ModelCategoryChip(this, ModelGrouping.All, _s.Get("Vm_CatChip_all"), GlyphFor(ModelGrouping.All), _s.Get("Vm_CatAllTip"));
        wanted.Add(_allChip);
        foreach (var (category, _) in counts)
        {
            var chip = Chips.FirstOrDefault(c => c.Id == category)
                ?? new ModelCategoryChip(this, category, _s.Get("Vm_CatChip_" + category), GlyphFor(category), _s.Get("Vm_Cat_" + category));
            wanted.Add(chip);
        }

        // A filter whose category is gone (for example after a download was removed) falls back to All.
        if (CategoryFilter.Length > 0 && wanted.All(c => c.Id != CategoryFilter))
        {
            CategoryFilter = ModelGrouping.All;
        }

        _settingChips = true;
        SyncList(Chips, wanted);
        foreach (var c in Chips) c.Selected = c.Id == CategoryFilter;
        _settingChips = false;
    }

    /// <summary>Makes <paramref name="target"/> equal <paramref name="desired"/> with the fewest moves, keeping shared items in place.</summary>
    internal static void SyncList<T>(ObservableCollection<T> target, IReadOnlyList<T> desired) where T : class
    {
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], desired[i])) continue;
            var at = -1;
            for (var j = i + 1; j < target.Count; j++)
            {
                if (ReferenceEquals(target[j], desired[i])) { at = j; break; }
            }

            if (at >= 0) target.Move(at, i); else target.Insert(i, desired[i]);
        }

        while (target.Count > desired.Count) target.RemoveAt(target.Count - 1);
    }
}
