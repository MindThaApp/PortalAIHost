using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using PortalAIHost.App.Services;
using PortalAIHost.App.ViewModels;

namespace PortalAIHost.App.Tests;

public class ModelGroupingTests
{
    private static GroupInput G(string id, string cat, double q = 50, bool rec = false, bool soon = false, string? name = null) =>
        new(id, name ?? id, cat, rec, q, soon);

    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    private static List<string> Ids(IReadOnlyList<GroupPlan> plan) => plan.SelectMany(p => p.VisibleIds).ToList();

    [Fact]
    public void Groups_follow_the_fixed_order_and_cut_to_the_top_few()
    {
        var rows = new[]
        {
            G("c1", "chat", 10), G("c2", "chat", 40), G("c3", "chat", 30), G("c4", "chat", 20), G("c5", "chat", 50),
            G("e1", "search", 5), G("v1", "video", 70, soon: true),
        };
        var plan = ModelGrouping.Plan(rows, ModelGrouping.All, "", None);
        Assert.Equal(["chat", "search", "video"], plan.Select(p => p.Category));
        Assert.Equal(["c5", "c2", "c3"], plan[0].VisibleIds);
        Assert.Equal(5, plan[0].Total);
    }

    [Fact]
    public void Expanding_a_group_shows_all_of_it()
    {
        var rows = Enumerable.Range(1, 6).Select(i => G("c" + i, "chat", i)).ToList();
        var plan = ModelGrouping.Plan(rows, ModelGrouping.All, "", new HashSet<string> { "chat" });
        Assert.Equal(6, plan.Single().VisibleIds.Count);
    }

    [Fact]
    public void The_recommended_model_leads_its_group_and_its_group_leads_the_page()
    {
        var rows = new[] { G("c1", "chat", 90), G("e1", "search", 10, rec: true), G("e2", "search", 80) };
        var plan = ModelGrouping.Plan(rows, ModelGrouping.All, "", None);
        Assert.Equal("search", plan[0].Category);
        Assert.Equal("e1", plan[0].VisibleIds[0]);
    }

    [Fact]
    public void Coming_soon_models_sort_after_usable_ones()
    {
        var rows = new[] { G("v1", "video", 90, soon: true), G("v2", "video", 10) };
        Assert.Equal(["v2", "v1"], ModelGrouping.Plan(rows, ModelGrouping.All, "", None).Single().VisibleIds);
    }

    [Fact]
    public void A_category_filter_keeps_only_that_category_and_shows_all_of_it()
    {
        var rows = Enumerable.Range(1, 6).Select(i => G("c" + i, "chat", i)).Append(G("e1", "search")).ToList();
        var plan = ModelGrouping.Plan(rows, "chat", "", None);
        Assert.Equal("chat", plan.Single().Category);
        Assert.Equal(6, plan.Single().VisibleIds.Count);
    }

    [Fact]
    public void Search_matches_every_word_in_name_id_or_category_ignoring_case()
    {
        var rows = new[] { G("a", "chat", name: "Qwen3 8B"), G("b", "chat", name: "Phi 4"), G("c", "video", name: "Wan 2.1") };
        Assert.Equal(["a"], Ids(ModelGrouping.Plan(rows, "", "qwen 8b", None)));
        Assert.Equal(["c"], Ids(ModelGrouping.Plan(rows, "", "VIDEO", None)));
        Assert.Empty(ModelGrouping.Plan(rows, "", "nothing", None));
        Assert.Equal(3, Ids(ModelGrouping.Plan(rows, "", "   ", None)).Count);
    }

    [Fact]
    public void Chip_counts_cover_only_present_categories()
    {
        var counts = ModelGrouping.Counts([G("a", "chat"), G("b", "chat"), G("c", "video")]);
        Assert.Equal([("chat", 2), ("video", 1)], counts);
    }
}

public class ModelsPageGroupingViewModelTests
{
    private static CatalogueModelSummary M(string id, string kind, string? category, double q = 50, bool soon = false, bool rec = false) => new()
    {
        Id = id, DisplayName = id.ToUpperInvariant(), Kind = kind, Category = category, Quality = q, SizeBytes = 1_000,
        FitsThisPc = true, ComingSoon = soon, Recommended = rec, MinVramGb = soon ? 8 : null, RecommendedVramGb = soon ? 12 : null,
    };

    private static async Task<ModelsViewModel> LoadAsync(Rig rig, IEnumerable<CatalogueModelSummary> extra)
    {
        foreach (var m in extra) rig.Host.AddModel(m);
        var vm = new ModelsViewModel(rig.Shell, rig.Ui, rig.Strings, rig.Dialogs);
        await vm.LoadAsync();
        await vm.RegroupAsync();
        return vm;
    }

    [Fact]
    public async Task Models_are_grouped_by_category_with_a_category_derived_from_the_kind_when_missing()
    {
        using var rig = new Rig(FakeScenario.Fresh);
        var vm = await LoadAsync(rig, [M("emb", "embedding", null), M("wan", "video", "video", soon: true)]);
        Assert.Equal(["chat", "search", "video"], vm.Groups.Select(g => g.CategoryId));
        Assert.Equal(["", "chat", "search", "video"], vm.Chips.Select(c => c.Id));
        Assert.True(vm.Chips[0].Selected);
    }

    [Fact]
    public async Task A_crowded_group_collapses_to_three_and_show_all_expands_it_without_replacing_the_group()
    {
        using var rig = new Rig(FakeScenario.Fresh);
        var vm = await LoadAsync(rig, Enumerable.Range(1, 5).Select(i => M("x" + i, "chat", "coding", q: i)));
        var coding = vm.Groups.Single(g => g.CategoryId == "coding");
        Assert.Equal(3, coding.Rows.Count);
        Assert.True(coding.CanToggle);
        Assert.Equal("Vm_ShowAll:5", coding.ToggleText);
        coding.ToggleAllCommand.Execute(null);
        Assert.Same(coding, vm.Groups.Single(g => g.CategoryId == "coding"));
        Assert.Equal(5, coding.Rows.Count);
        Assert.Equal("Vm_ShowFewer", coding.ToggleText);
        coding.ToggleAllCommand.Execute(null);
        Assert.Equal(3, coding.Rows.Count);
    }

    [Fact]
    public async Task The_category_filter_and_search_narrow_the_list_and_the_all_chip_restores_it()
    {
        using var rig = new Rig(FakeScenario.Fresh);
        var vm = await LoadAsync(rig, [M("wan", "video", "video", soon: true), M("emb", "embedding", "search")]);
        vm.Chips.Single(c => c.Id == "video").Selected = true;
        Assert.Equal("video", vm.CategoryFilter);
        Assert.Equal(["video"], vm.Groups.Select(g => g.CategoryId));
        Assert.True(vm.Chips.Single(c => c.Id == "video").Selected);
        Assert.False(vm.Chips[0].Selected);

        vm.Chips[0].Selected = true;
        Assert.True(vm.Groups.Count >= 3);

        vm.SearchText = "emb";
        Assert.Equal(["search"], vm.Groups.Select(g => g.CategoryId));
        vm.SearchText = "zzz";
        Assert.Empty(vm.Groups);
        Assert.True(vm.NoMatches);
        vm.SearchText = "";
        Assert.False(vm.NoMatches);
    }

    [Fact]
    public async Task Clicking_the_selected_chip_again_keeps_it_selected()
    {
        using var rig = new Rig(FakeScenario.Fresh);
        var vm = await LoadAsync(rig, [M("wan", "video", "video", soon: true)]);
        var video = vm.Chips.Single(c => c.Id == "video");
        video.Selected = true;
        video.Selected = false;
        Assert.True(video.Selected);
        Assert.Equal("video", vm.CategoryFilter);
    }

    [Fact]
    public async Task Coming_soon_rows_cannot_be_downloaded_and_say_so()
    {
        using var rig = new Rig(FakeScenario.Fresh);
        var vm = await LoadAsync(rig, [M("wan", "video", "video", soon: true)]);
        var row = vm.Models.Single(m => m.Id == "wan");
        Assert.True(row.ComingSoon);
        Assert.False(row.CanDownload);
        Assert.False(row.TooBig);
        Assert.Equal("Vm_ComingSoon", row.Badge);
        Assert.Equal("Vm_VramBoth:8|12", row.VramText);
    }

    [Fact]
    public async Task A_large_list_is_grouped_off_the_ui_thread_and_only_the_last_request_wins()
    {
        using var rig = new Rig(FakeScenario.Fresh);
        var vm = await LoadAsync(rig, Enumerable.Range(0, 80).Select(i => M("m" + i, "chat", i % 2 == 0 ? "chat" : "coding", q: i)));
        Assert.True(vm.Models.Count > ModelsViewModel.OffThreadThreshold);
        Assert.Equal(3, vm.Groups.Single(g => g.CategoryId == "coding").Rows.Count);
        vm.SearchText = "m1";
        vm.SearchText = "m7";
        await vm.RegroupAsync();
        Assert.All(vm.Groups.SelectMany(g => g.Rows), r => Assert.Contains("M7", r.Name));
        Assert.True(vm.Groups.SelectMany(g => g.Rows).Count() >= 10);
    }
}
