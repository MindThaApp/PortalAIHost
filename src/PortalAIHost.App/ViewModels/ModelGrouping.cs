using Portal.AiHost.Contracts;

namespace PortalAIHost.App.ViewModels;

/// <summary>What the grouping needs to know about one model row. Plain data, so it can be planned on any thread.</summary>
public sealed record GroupInput(string Id, string Name, string Category, bool Recommended, double Quality, bool ComingSoon);

/// <summary>One category group: the row ids to show (best first) and how many models the group holds in all.</summary>
public sealed record GroupPlan(string Category, IReadOnlyList<string> VisibleIds, int Total);

/// <summary>
/// Pure grouping, filtering and search for the Models page. No UI types and no shared state, so a large list can be
/// planned off the UI thread.
/// </summary>
public static class ModelGrouping
{
    /// <summary>Models shown per category before "Show all".</summary>
    public const int CollapsedCount = 3;

    /// <summary>The "all categories" filter.</summary>
    public const string All = "";

    /// <summary>True when the model matches every word of the search text (name, id or category id; case-insensitive).</summary>
    public static bool Matches(GroupInput row, string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        foreach (var word in search.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!row.Name.Contains(word, StringComparison.OrdinalIgnoreCase)
                && !row.Id.Contains(word, StringComparison.OrdinalIgnoreCase)
                && !row.Category.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Best first: recommended for this PC, then usable before coming soon, then quality, then name.</summary>
    public static IOrderedEnumerable<GroupInput> Order(IEnumerable<GroupInput> rows) =>
        rows.OrderByDescending(r => r.Recommended)
            .ThenBy(r => r.ComingSoon)
            .ThenByDescending(r => r.Quality)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Id, StringComparer.Ordinal);

    /// <summary>
    /// Groups the rows by category. Groups holding a recommended model come first, the rest follow in the fixed
    /// category order. A group is cut to the top <paramref name="topN"/> unless it is expanded, a search is active or a
    /// single category is chosen (the person is already looking at it).
    /// </summary>
    public static IReadOnlyList<GroupPlan> Plan(
        IReadOnlyList<GroupInput> rows, string? categoryFilter, string? search, IReadOnlySet<string> expanded, int topN = CollapsedCount)
    {
        var showAll = !string.IsNullOrWhiteSpace(search) || !string.IsNullOrEmpty(categoryFilter);
        var matching = rows
            .Where(r => string.IsNullOrEmpty(categoryFilter) || r.Category == categoryFilter)
            .Where(r => Matches(r, search))
            .ToList();
        var plans = new List<(bool HasRecommended, int Order, GroupPlan Plan)>();
        foreach (var g in matching.GroupBy(r => r.Category))
        {
            var ordered = Order(g).ToList();
            var cut = showAll || expanded.Contains(g.Key) ? ordered : ordered.Take(topN).ToList();
            var order = ModelCategories.Ordered.ToList().IndexOf(g.Key);
            plans.Add((ordered[0].Recommended, order < 0 ? int.MaxValue : order,
                new GroupPlan(g.Key, cut.Select(r => r.Id).ToList(), ordered.Count)));
        }

        return plans.OrderByDescending(p => p.HasRecommended).ThenBy(p => p.Order)
            .ThenBy(p => p.Plan.Category, StringComparer.Ordinal).Select(p => p.Plan).ToList();
    }

    /// <summary>Categories present in the rows with their model counts, in display order.</summary>
    public static IReadOnlyList<(string Category, int Count)> Counts(IReadOnlyList<GroupInput> rows) =>
        rows.GroupBy(r => r.Category)
            .Select(g => (Category: g.Key, Count: g.Count(), Order: ModelCategories.Ordered.ToList().IndexOf(g.Key)))
            .OrderBy(x => x.Order < 0 ? int.MaxValue : x.Order).ThenBy(x => x.Category, StringComparer.Ordinal)
            .Select(x => (x.Category, x.Count)).ToList();
}
