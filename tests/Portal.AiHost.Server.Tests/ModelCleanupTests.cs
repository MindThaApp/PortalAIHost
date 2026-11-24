using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Platform.Windows.Pipes;
using PortalAIHost.Service;

namespace Portal.AiHost.Server.Tests;

/// <summary>"Free up space" at the model center: damaged models, the busy guard, and the second look before deleting.</summary>
public sealed partial class ModelManagementTests
{
    [Fact(Timeout = 30000)]
    public async Task Cleanup_lists_only_damaged_models_that_are_not_in_use_and_removes_only_the_ticked_ones()
    {
        var (root, store, h, fileA, fileB, oldRoot, a, b) = await TwoModelsAsync();
        using var _r = root;
        using var _s = store;
        h.Notes.MarkDamaged("model-a", "model-a.gguf does not match the model list.");
        h.Active[ModelKinds.Chat] = "model-a"; // in use: never offered
        Assert.Empty((await h.Center.ScanCleanupAsync(CancellationToken.None)).Items);

        h.Active[ModelKinds.Chat] = null;
        h.Notes.MarkDamaged("model-b", "model-b.gguf does not match the model list.");
        var scan = await h.Center.ScanCleanupAsync(CancellationToken.None);
        Assert.Equal(["model-a", "model-b"], scan.Items.Select(i => i.Label).Order().ToArray());
        Assert.All(scan.Items, i => Assert.Equal(CleanupKinds.Damaged, i.Kind));
        Assert.True(File.Exists(fileA) && File.Exists(fileB)); // a scan deletes nothing

        var pickB = scan.Items.Single(i => i.Label == "model-b").Id;
        var result = await h.Center.CleanupAsync([pickB], CancellationToken.None);

        Assert.Equal(["model-b"], result.Removed.ToArray());
        Assert.Empty(result.Skipped);
        Assert.False(File.Exists(fileB));
        Assert.True(File.Exists(fileA));
        Assert.Null(h.Notes.DamagedReason("model-b"));
        Assert.NotNull(await store.GetAsync("model-a", CancellationToken.None));
    }

    [Fact(Timeout = 30000)]
    public async Task A_model_that_came_into_use_after_the_scan_is_skipped()
    {
        var (root, store, h, fileA, fileB, oldRoot, a, b) = await TwoModelsAsync();
        using var _r = root;
        using var _s = store;
        h.Notes.MarkDamaged("model-a", "x");
        var scan = await h.Center.ScanCleanupAsync(CancellationToken.None);
        h.Active[ModelKinds.Chat] = "model-a"; // the user switched to it meanwhile

        var result = await h.Center.CleanupAsync([scan.Items.Single().Id], CancellationToken.None);

        Assert.Empty(result.Removed);
        Assert.Single(result.Skipped);
        Assert.True(File.Exists(fileA));
    }

    [Fact(Timeout = 30000)]
    public async Task Healthy_models_and_made_up_ids_are_never_removed()
    {
        var (root, store, h, fileA, fileB, oldRoot, a, b) = await TwoModelsAsync();
        using var _r = root;
        using var _s = store;

        var result = await h.Center.CleanupAsync(["damaged|model-a|" + a.Length, "model-a", "part|model-a|x|y|1|0|1"], CancellationToken.None);

        Assert.Empty(result.Removed);
        Assert.Equal(3, result.Skipped.Count);
        Assert.True(File.Exists(fileA) && File.Exists(fileB));
    }

    [Fact(Timeout = 30000)]
    public async Task While_a_move_runs_scan_reports_busy_and_clean_is_refused()
    {
        var (root, store, h, fileA, fileB, oldRoot, a, b) = await TwoModelsAsync();
        using var _r = root;
        using var _s = store;
        h.Notes.MarkDamaged("model-a", "x");
        var gate = new TaskCompletionSource();
        h.OnStop = async ct => await gate.Task.WaitAsync(ct);
        var running = await h.Center.StartMoveAsync(root.Sub("d1"), CancellationToken.None);

        var scan = await h.Center.ScanCleanupAsync(CancellationToken.None);
        Assert.NotNull(scan.BusyReason);
        Assert.Empty(scan.Items);
        await Assert.ThrowsAsync<ControlPipeException>(() => h.Center.CleanupAsync(["damaged|model-a|1"], CancellationToken.None));
        Assert.True(File.Exists(fileA));

        gate.SetResult();
        await running;
    }

    [Fact(Timeout = 30000)]
    public async Task Old_copies_after_a_move_are_offered_and_the_earlier_folder_is_forgotten_once_they_are_gone()
    {
        var (root, store, h, fileA, fileB, oldRoot, a, b) = await TwoModelsAsync();
        using var _r = root;
        using var _s = store;
        await store.MoveStoreAsync(root.Sub("d1"), null, CancellationToken.None);

        var scan = await h.Center.ScanCleanupAsync(CancellationToken.None);
        var old = Assert.Single(scan.Items);
        Assert.Equal(CleanupKinds.OldCopies, old.Kind);
        Assert.Equal(a.Length + b.Length, old.Bytes);

        var result = await h.Center.CleanupAsync([old.Id], CancellationToken.None);

        Assert.Equal(a.Length + b.Length, result.FreedBytes);
        Assert.False(File.Exists(fileA));
        Assert.False(File.Exists(fileB));
        Assert.Contains(oldRoot, h.Forgotten, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(a, File.ReadAllBytes(await PathOfAsync(store, "model-a")));
    }
}
