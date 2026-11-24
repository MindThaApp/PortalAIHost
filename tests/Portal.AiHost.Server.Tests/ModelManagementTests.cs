using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Catalogue;
using Portal.AiHost.Core.Store;
using Portal.AiHost.FakeEngine;
using Portal.AiHost.Platform.Windows.Pipes;
using PortalAIHost.Service;

namespace Portal.AiHost.Server.Tests;

/// <summary>Remove the active model, move the models folder, check files and open folder, in the host.</summary>
internal static class RootExtensions
{
    public static string Sub(this TestRoot root, string name)
    {
        var p = Path.Combine(root.Path, name);
        Directory.CreateDirectory(p);
        return p;
    }
}

public sealed partial class ModelManagementTests
{
    private const long GiB = 1L << 30;

    private static CatalogueEntry Entry(string id, byte[] body) => new()
    {
        Id = id, DisplayName = id, Kind = ModelKinds.Chat, Quality = 3, HfRepo = "test/" + id,
        HfCommit = new string('a', 40), ContextMax = 8192, ContextDefault = 4096,
        Licence = new CatalogueLicence { Spdx = "apache-2.0", Url = "https://example.invalid/licence" },
        Advice = new CatalogueAdvice { Summary = "Good all-rounder.", GoodFor = ["everyday chat"], Caveats = ["slow without a graphics card"] },
        Memory = new ModelMemory { WeightsBytes = 2 * GiB, NonExpertBytes = 2 * GiB, NLayers = 24, KvBytesPer1kCtx = 64L << 20, ComputeOverheadBytes = 256L << 20 },
        Files = [new CatalogueFile { Name = id + ".gguf", Size = body.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(body)), Urls = ["https://example.invalid/" + id + ".gguf"] }],
    };

    private static async Task InstallAsync(ModelStore store, CatalogueEntry entry, byte[] body)
    {
        using var _ = store.AcquireDownload(entry.Id);
        await store.EnsureModelAsync(entry.Id, entry.DisplayName, entry.Kind, entry.Engine, CancellationToken.None);
        var part = await store.BeginFileAsync(entry.Id, entry.Files[0].Name, CancellationToken.None);
        await File.WriteAllBytesAsync(part, body);
        await store.CommitFileAsync(entry.Id, part, entry.Files[0].Sha256, CancellationToken.None);
    }

    private static async Task<string> PathOfAsync(ModelStore store, string id) =>
        (await store.GetAsync(id, CancellationToken.None))!.Files.Single().Path;

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "timed out");
    }

    /// <summary>A model center over a real store with recording hooks.</summary>
    private sealed class Harness
    {
        public ConcurrentQueue<StoreProgressEvent> Events { get; } = new();
        public ConcurrentQueue<DownloadProgressEvent> Downloads { get; } = new();
        public Dictionary<string, string?> Active { get; } = new();
        public ModelNotes Notes { get; } = new(null);
        public ConcurrentQueue<string> Calls { get; } = new();
        public volatile bool Loaded;
        public Func<CancellationToken, Task>? OnStop { get; set; }
        public Func<string, CancellationToken, Task>? OnClear { get; set; }
        public Func<StoreProgressEvent, Task>? OnPush { get; set; }
        public Func<string, long> Free { get; set; } = _ => 100 * GiB;
        public (string Parent, string? OldRoot)? Moved { get; private set; }
        public int ApplyFailures { get; set; }
        public int ApplyCalls { get; private set; }
        public ConcurrentQueue<string> Remembered { get; } = new();
        public ConcurrentQueue<string> Forgotten { get; } = new();
        public List<string?> Maintenance { get; } = new();
        public ModelCenter Center { get; }

        public Harness(ModelStore store, IReadOnlyList<CatalogueEntry> catalogue, bool withClear = true)
        {
            Center = new ModelCenter(store, catalogue, null, null, () => new HostSettings(),
                kind => Active.GetValueOrDefault(kind), e => { Downloads.Enqueue(e); return Task.CompletedTask; }, _ => Loaded)
            {
                Hooks = new ModelCenterHooks
                {
                    Notes = Notes,
                    UnloadWait = TimeSpan.FromMilliseconds(600),
                    FreeBytesOf = p => Free(p),
                    ClearActive = withClear ? async (kind, ct) =>
                    {
                        Calls.Enqueue("clear:" + kind);
                        if (OnClear is { } c) { await c(kind, ct); }
                        Active[kind] = null;
                    } : null,
                    Activate = async (id, ct) =>
                    {
                        Calls.Enqueue("activate:" + id);
                        var m = (await store.GetAsync(id, ct))!;
                        Active[m.Kind] = id;
                    },
                    StopEngines = async ct =>
                    {
                        Calls.Enqueue("stop");
                        if (OnStop is { } s) { await s(ct); }
                    },
                    RefreshActive = _ => { Calls.Enqueue("refresh"); return Task.CompletedTask; },
                    ApplyMovedFolder = (p, o) =>
                    {
                        ApplyCalls++;
                        if (ApplyFailures-- > 0) { throw new IOException("settings are locked"); }
                        Moved = (p, o);
                    },
                    RememberFolder = f => Remembered.Enqueue(f),
                    ForgetFolder = f => Forgotten.Enqueue(f),
                    SetMaintenance = m => { lock (Maintenance) { Maintenance.Add(m); } },
                    PushStore = async e =>
                    {
                        Events.Enqueue(e);
                        if (OnPush is { } p) { await p(e); }
                    },
                },
            };
        }

        public StoreProgressEvent Last => Events.Last();
    }

    private static (TestRoot Root, ModelStore Store) NewStore()
    {
        var root = new TestRoot();
        return (root, ModelStore.OpenOrCreate(Path.Combine(root.Path, "models")));
    }

    // ---- remove, including the active model ------------------------------------------------------

    [Fact(Timeout = 30000)]
    public async Task Removing_the_active_model_clears_it_waits_for_the_engine_then_removes_through_the_store()
    {
        var (root, store) = NewStore();
        using var _r = root;
        using var _s = store;
        var a = RandomNumberGenerator.GetBytes(2048);
        var b = RandomNumberGenerator.GetBytes(2048);
        var ea = Entry("model-a", a);
        var eb = Entry("model-b", b);
        await InstallAsync(store, ea, a);
        await InstallAsync(store, eb, b);
        var fileA = await PathOfAsync(store, "model-a");
        var fileB = await PathOfAsync(store, "model-b");
        await store.SetInUseAsync("model-a", true, CancellationToken.None);
        var h = new Harness(store, [ea, eb]) { Loaded = true };
        h.Active[ModelKinds.Chat] = "model-a";
        var stillThereWhenReleased = false;
        h.OnClear = async (_, _) =>
        {
            // The engine takes a moment to let go of the file; the store must not be asked to delete before that.
            await Task.Delay(120);
            stillThereWhenReleased = File.Exists(fileA);
            h.Loaded = false;
            await store.SetInUseAsync("model-a", false, CancellationToken.None);
        };

        await h.Center.RemoveAsync("model-a", CancellationToken.None);

        Assert.True(stillThereWhenReleased);
        Assert.Equal(["clear:chat"], h.Calls.ToArray());
        Assert.False(File.Exists(fileA));
        Assert.Null(await store.GetAsync("model-a", CancellationToken.None));
        Assert.True(File.Exists(fileB)); // the other model is untouched
        Assert.Contains(h.Downloads, d => d.CatalogueId == "model-a" && d.State == "removing");
        Assert.Equal("removed", h.Downloads.Last().State);
        Assert.Null(h.Active[ModelKinds.Chat]);
    }

    [Fact(Timeout = 30000)]
    public async Task Removing_the_active_model_can_switch_to_another_installed_model_first()
    {
        var (root, store) = NewStore();
        using var _r = root;
        using var _s = store;
        var a = RandomNumberGenerator.GetBytes(2048);
        var b = RandomNumberGenerator.GetBytes(2048);
        var ea = Entry("model-a", a);
        var eb = Entry("model-b", b);
        await InstallAsync(store, ea, a);
        await InstallAsync(store, eb, b);
        var h = new Harness(store, [ea, eb]);
        h.Active[ModelKinds.Chat] = "model-a";

        await h.Center.RemoveAsync("model-a", CancellationToken.None, switchToId: "model-b");

        Assert.Equal(["activate:model-b"], h.Calls.ToArray());
        Assert.Equal("model-b", h.Active[ModelKinds.Chat]);
        Assert.Null(await store.GetAsync("model-a", CancellationToken.None));
        Assert.NotNull(await store.GetAsync("model-b", CancellationToken.None));
    }

    [Fact(Timeout = 30000)]
    public async Task A_switch_target_that_is_not_installed_or_is_the_same_model_removes_nothing()
    {
        var (root, store) = NewStore();
        using var _r = root;
        using var _s = store;
        var a = RandomNumberGenerator.GetBytes(2048);
        var ea = Entry("model-a", a);
        await InstallAsync(store, ea, a);
        var h = new Harness(store, [ea]);
        h.Active[ModelKinds.Chat] = "model-a";
        foreach (var target in new[] { "nope", "model-a" })
        {
            var ex = await Assert.ThrowsAsync<ControlPipeException>(() => h.Center.RemoveAsync("model-a", CancellationToken.None, target));
            Assert.Equal(ErrorCodes.InvalidRequest, ex.Code);
        }

        Assert.NotNull(await store.GetAsync("model-a", CancellationToken.None));
        Assert.True(File.Exists(await PathOfAsync(store, "model-a")));
        Assert.Empty(h.Calls);
    }

    [Fact(Timeout = 30000)]
    public async Task If_the_engine_never_lets_go_the_active_model_is_not_deleted()
    {
        var (root, store) = NewStore();
        using var _r = root;
        using var _s = store;
        var a = RandomNumberGenerator.GetBytes(2048);
        var ea = Entry("model-a", a);
        await InstallAsync(store, ea, a);
        await store.SetInUseAsync("model-a", true, CancellationToken.None);
        var h = new Harness(store, [ea]) { Loaded = true };
        h.Active[ModelKinds.Chat] = "model-a";

        var ex = await Assert.ThrowsAsync<ControlPipeException>(() => h.Center.RemoveAsync("model-a", CancellationToken.None));

        Assert.Equal(ErrorCodes.InvalidRequest, ex.Code);
        Assert.True(File.Exists(await PathOfAsync(store, "model-a")));
        Assert.NotNull(await store.GetAsync("model-a", CancellationToken.None));
    }

    [Fact(Timeout = 30000)]
    public async Task A_refused_removal_is_announced_so_the_window_can_roll_back_and_nothing_is_deleted()
    {
        var (root, store) = NewStore();
        using var _r = root;
        using var _s = store;
        var a = RandomNumberGenerator.GetBytes(2048);
        var ea = Entry("model-a", a);
        await InstallAsync(store, ea, a);
        await store.SetInUseAsync("model-a", true, CancellationToken.None); // in use by an engine the host does not know of
        var h = new Harness(store, [ea]);

        await Assert.ThrowsAsync<ControlPipeException>(() => h.Center.RemoveAsync("model-a", CancellationToken.None));

        Assert.Equal("remove_failed", h.Downloads.Last().State);
        Assert.True(File.Exists(await PathOfAsync(store, "model-a")));
    }

    // ---- listing details ---------------------------------------------------------------------------

    [Fact(Timeout = 30000)]
    public async Task The_listing_carries_size_totals_licence_advice_last_use_and_damage()
    {
        var (root, store) = NewStore();
        using var _r = root;
        using var _s = store;
        var a = RandomNumberGenerator.GetBytes(3000);
        var ea = Entry("model-a", a);
        await InstallAsync(store, ea, a);
        var h = new Harness(store, [ea]);
        var when = new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);
        h.Notes.MarkUsed("model-a", when);
        h.Notes.MarkDamaged("model-a", "x");

        var listing = await h.Center.ListAsync(CancellationToken.None);
        var row = Assert.Single(listing.Models);

        Assert.Equal(3000, listing.ModelsBytes);
        Assert.Equal(when, row.LastUsedAt);
        Assert.True(row.Damaged);
        Assert.Equal("apache-2.0", row.LicenceName);
        Assert.Equal("https://example.invalid/licence", row.LicenceUrl);
        Assert.Equal("Good all-rounder.", row.AdviceSummary);
        Assert.Equal(["everyday chat"], row.GoodFor);
        Assert.Equal(["slow without a graphics card"], row.Caveats);
    }

    [Fact]
    public async Task The_models_folder_is_given_for_the_window_to_open()
    {
        var (root, store) = NewStore();
        using var _r = root;
        using var _s = store;
        var h = new Harness(store, []);
        Assert.Equal(store.Location.Path, h.Center.StoreFolder());
        var none = new ModelCenter(null, null, null, null, () => new HostSettings(), _ => null, _ => Task.CompletedTask);
        Assert.Throws<ControlPipeException>(() => none.StoreFolder());
        await Task.CompletedTask;
    }

    // ---- move ---------------------------------------------------------------------------------------

    private static async Task<(TestRoot Root, ModelStore Store, Harness H, string OldFileA, string OldFileB, string OldRoot, byte[] A, byte[] B)> TwoModelsAsync()
    {
        var (root, store) = NewStore();
        var a = RandomNumberGenerator.GetBytes(50_000);
        var b = RandomNumberGenerator.GetBytes(60_000);
        var ea = Entry("model-a", a);
        var eb = Entry("model-b", b);
        await InstallAsync(store, ea, a);
        await InstallAsync(store, eb, b);
        return (root, store, new Harness(store, [ea, eb]), await PathOfAsync(store, "model-a"), await PathOfAsync(store, "model-b"),
            store.Location.Path, a, b);
    }

    private static void AssertOldStoreIntact(string oldRoot, string fileA, string fileB, byte[] a, byte[] b)
    {
        Assert.True(Directory.Exists(oldRoot));
        Assert.True(File.Exists(Path.Combine(oldRoot, "store.json")));
        Assert.Equal(a, File.ReadAllBytes(fileA));
        Assert.Equal(b, File.ReadAllBytes(fileB));
    }

    [Fact(Timeout = 30000)]
    public async Task Moving_copies_verifies_switches_then_removes_only_the_old_copies()
    {
        var (root, store, h, fileA, fileB, oldRoot, a, b) = await TwoModelsAsync();
        using var _r = root;
        using var _s = store;
        var stray = Path.Combine(oldRoot, "not-ours.txt");
        File.WriteAllText(stray, "user file");
        var parent = root.Sub("D-drive");

        await await h.Center.StartMoveAsync(parent, CancellationToken.None);

        Assert.Equal(StoreStates.Done, h.Last.State);
        var newRoot = ModelStore.StoreFolderIn(parent);
        Assert.Equal(newRoot, store.Location.Path, ignoreCase: true);
        var newA = await PathOfAsync(store, "model-a");
        Assert.StartsWith(newRoot, newA, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(a, File.ReadAllBytes(newA));
        Assert.Equal(b, File.ReadAllBytes(await PathOfAsync(store, "model-b")));
        Assert.False(File.Exists(fileA));
        Assert.False(File.Exists(fileB));
        Assert.True(File.Exists(stray)); // not listed by the old store: never touched
        Assert.Equal(parent, h.Moved!.Value.Parent);
        Assert.Equal(oldRoot, h.Moved.Value.OldRoot, ignoreCase: true);
        Assert.Equal(["stop", "refresh"], h.Calls.ToArray());
        Assert.Contains(h.Events, e => e.State == StoreStates.RemovingOld);
        Assert.Contains(h.Events, e => e.State == StoreStates.Running && e.Fraction > 0);
        // The listing still works and now names the new folder.
        Assert.Equal(newRoot, (await h.Center.ListAsync(CancellationToken.None)).ModelsFolder, ignoreCase: true);
    }

    [Fact(Timeout = 30000)]
    public async Task A_move_to_a_drive_without_enough_space_is_refused_and_says_how_much_is_needed()
    {
        var (root, store, h, fileA, fileB, oldRoot, a, b) = await TwoModelsAsync();
        using var _r = root;
        using var _s = store;
        h.Free = _ => GiB; // needs 2 GB headroom plus the models
        var parent = root.Sub("small-drive");

        var ex = await Assert.ThrowsAsync<ControlPipeException>(() => h.Center.StartMoveAsync(parent, CancellationToken.None));

        Assert.Equal(ErrorCodes.InvalidRequest, ex.Code);
        Assert.Contains("needs", ex.Message);
        Assert.Contains("GB", ex.Message);
        Assert.Empty(h.Events);
        Assert.Empty(Directory.GetFileSystemEntries(parent)); // no folder was created there
        AssertOldStoreIntact(oldRoot, fileA, fileB, a, b);
        // The refusal does not leave the "busy" state behind.
        h.Free = _ => 100 * GiB;
        await await h.Center.StartMoveAsync(parent, CancellationToken.None);
        Assert.Equal(StoreStates.Done, h.Last.State);
    }

    [Fact(Timeout = 30000)]
    public async Task A_move_into_the_current_folder_or_a_folder_that_already_has_a_store_is_refused()
    {
        var (root, store, h, fileA, fileB, oldRoot, a, b) = await TwoModelsAsync();
        using var _r = root;
        using var _s = store;
        var inside = Path.Combine(oldRoot, "sub");
        Directory.CreateDirectory(inside);
        var taken = root.Sub("taken");
        Directory.CreateDirectory(Path.Combine(taken, ModelStore.UserFolderName));

        foreach (var bad in new[] { inside, taken, Path.Combine(root.Path, "missing"), "relative" })
        {
            var ex = await Assert.ThrowsAsync<ControlPipeException>(() => h.Center.StartMoveAsync(bad, CancellationToken.None));
            Assert.Equal(ErrorCodes.InvalidRequest, ex.Code);
        }

        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(taken, ModelStore.UserFolderName)));
        AssertOldStoreIntact(oldRoot, fileA, fileB, a, b);
    }

    [Fact(Timeout = 30000)]
    public async Task Cancelling_while_the_engines_stop_leaves_the_old_store_intact()
    {
        var (root, store, h, fileA, fileB, oldRoot, a, b) = await TwoModelsAsync();
        using var _r = root;
        using var _s = store;
        var parent = root.Sub("target");
        h.OnStop = async ct =>
        {
            h.Center.CancelOperation();
            await Task.Delay(Timeout.Infinite, ct);
        };

        await await h.Center.StartMoveAsync(parent, CancellationToken.None);

        Assert.Equal(StoreStates.Cancelled, h.Last.State);
        Assert.Equal(oldRoot, store.Location.Path, ignoreCase: true);
        Assert.Empty(Directory.GetFileSystemEntries(parent));
        Assert.Null(h.Moved);
        AssertOldStoreIntact(oldRoot, fileA, fileB, a, b);
    }

    [Fact(Timeout = 30000)]
    public async Task Cancelling_in_the_middle_of_copying_undoes_the_copy_and_leaves_the_old_store_intact()
    {
        var (root, store, h, fileA, fileB, oldRoot, a, b) = await TwoModelsAsync();
        using var _r = root;
        using var _s = store;
        var parent = root.Sub("target");
        h.OnPush = e =>
        {
            // The first finished file reports progress; cancel before the second is copied.
            if (e is { State: StoreStates.Running, Fraction: > 0 and < 1 })
            {
                h.Center.CancelOperation();
            }

            return Task.CompletedTask;
        };

        await await h.Center.StartMoveAsync(parent, CancellationToken.None);

        Assert.Equal(StoreStates.Cancelled, h.Last.State);
        Assert.Equal(oldRoot, store.Location.Path, ignoreCase: true);
        Assert.Null(h.Moved);
        Assert.Empty(Directory.EnumerateFiles(parent, "*.gguf*", SearchOption.AllDirectories));
        AssertOldStoreIntact(oldRoot, fileA, fileB, a, b);
        Assert.NotNull(await store.GetAsync("model-a", CancellationToken.None));
        Assert.NotNull(await store.GetAsync("model-b", CancellationToken.None));
        // The store is usable again: a second move works.
        h.OnPush = null;
        await await h.Center.StartMoveAsync(parent, CancellationToken.None);
        Assert.Equal(StoreStates.Done, h.Last.State);
    }

    [Fact(Timeout = 30000)]
    public async Task Cancelling_after_the_switch_does_not_undo_it_and_the_new_copies_stay()
    {
        var (root, store, h, fileA, fileB, oldRoot, a, b) = await TwoModelsAsync();
        using var _r = root;
        using var _s = store;
        var parent = root.Sub("target");
        h.OnPush = e =>
        {
            if (e.State == StoreStates.RemovingOld)
            {
                h.Center.CancelOperation();
            }

            return Task.CompletedTask;
        };

        await await h.Center.StartMoveAsync(parent, CancellationToken.None);

        Assert.Equal(StoreStates.Done, h.Last.State);
        Assert.Equal(a, File.ReadAllBytes(await PathOfAsync(store, "model-a")));
        Assert.Equal(b, File.ReadAllBytes(await PathOfAsync(store, "model-b")));
    }

    [Fact(Timeout = 30000)]
    public async Task While_a_move_runs_a_second_move_or_a_check_is_refused()
    {
        var (root, store, h, fileA, fileB, oldRoot, a, b) = await TwoModelsAsync();
        using var _r = root;
        using var _s = store;
        var parent = root.Sub("target");
        var gate = new TaskCompletionSource();
        h.OnStop = async ct => await gate.Task.WaitAsync(ct);

        var running = await h.Center.StartMoveAsync(parent, CancellationToken.None);

        await Assert.ThrowsAsync<ControlPipeException>(() => h.Center.StartMoveAsync(root.Sub("other"), CancellationToken.None));
        await Assert.ThrowsAsync<ControlPipeException>(() => h.Center.StartVerifyAsync("model-a", CancellationToken.None));
        // The page keeps its list while the store is busy copying.
        Assert.Equal(2, (await h.Center.ListAsync(CancellationToken.None)).Models.Count);
        gate.SetResult();
        await running;
        Assert.Equal(StoreStates.Done, h.Last.State);
    }

    [Fact(Timeout = 30000)]
    public async Task A_move_that_fails_while_copying_is_undone_and_reported()
    {
        var (root, store, h, fileA, fileB, oldRoot, a, b) = await TwoModelsAsync();
        using var _r = root;
        using var _s = store;
        var parent = root.Sub("target");
        h.OnStop = _ => throw new IOException("drive went away");

        await await h.Center.StartMoveAsync(parent, CancellationToken.None);

        Assert.Equal(StoreStates.Failed, h.Last.State);
        Assert.Contains("drive went away", h.Last.Message);
        Assert.Contains("where they were", h.Last.Message);
        AssertOldStoreIntact(oldRoot, fileA, fileB, a, b);
    }

    // ---- check files --------------------------------------------------------------------------------

    [Fact(Timeout = 30000)]
    public async Task A_clean_check_reports_done_and_clears_an_old_damaged_mark()
    {
        var (root, store) = NewStore();
        using var _r = root;
        using var _s = store;
        var a = RandomNumberGenerator.GetBytes(300_000);
        var ea = Entry("model-a", a);
        await InstallAsync(store, ea, a);
        var h = new Harness(store, [ea]);
        h.Notes.MarkDamaged("model-a", "old");

        await await h.Center.StartVerifyAsync("model-a", CancellationToken.None);

        Assert.Equal(StoreStates.Done, h.Last.State);
        Assert.Equal("model-a", h.Last.CatalogueId);
        Assert.Null(h.Notes.DamagedReason("model-a"));
    }

    [Fact(Timeout = 30000)]
    public async Task A_damaged_file_is_marked_damaged_and_nothing_is_deleted_or_changed()
    {
        var (root, store) = NewStore();
        using var _r = root;
        using var _s = store;
        var a = RandomNumberGenerator.GetBytes(300_000);
        var ea = Entry("model-a", a);
        await InstallAsync(store, ea, a);
        var file = await PathOfAsync(store, "model-a");
        var broken = (byte[])a.Clone();
        broken[1234] ^= 0xFF; // same size, different content
        File.WriteAllBytes(file, broken);
        var h = new Harness(store, [ea]);

        await await h.Center.StartVerifyAsync("model-a", CancellationToken.None);

        Assert.Equal(StoreStates.Damaged, h.Last.State);
        Assert.Contains("does not match", h.Last.Message);
        Assert.NotNull(h.Notes.DamagedReason("model-a"));
        Assert.Equal(broken, File.ReadAllBytes(file)); // not repaired, not deleted
        Assert.NotNull(await store.GetAsync("model-a", CancellationToken.None));
        var row = Assert.Single((await h.Center.ListAsync(CancellationToken.None)).Models);
        Assert.True(row.Damaged);
        Assert.True(row.Installed);
    }

    [Fact(Timeout = 30000)]
    public async Task A_missing_file_counts_as_damaged_and_a_downloaded_replacement_clears_the_mark()
    {
        var (root, store) = NewStore();
        using var _r = root;
        using var _s = store;
        var a = RandomNumberGenerator.GetBytes(10_000);
        var ea = Entry("model-a", a);
        await InstallAsync(store, ea, a);
        File.Delete(await PathOfAsync(store, "model-a")); // simulate the user deleting it behind the host's back
        var h = new Harness(store, [ea]);

        await await h.Center.StartVerifyAsync("model-a", CancellationToken.None);

        Assert.Equal(StoreStates.Damaged, h.Last.State);
        Assert.Contains("missing", h.Last.Message);
    }

    [Fact(Timeout = 30000)]
    public async Task A_check_can_be_cancelled_and_files_you_added_yourself_cannot_be_checked()
    {
        var (root, store) = NewStore();
        using var _r = root;
        using var _s = store;
        var a = RandomNumberGenerator.GetBytes(300_000);
        var ea = Entry("model-a", a);
        await InstallAsync(store, ea, a);
        var outside = Path.Combine(root.Path, "mine.gguf");
        File.WriteAllBytes(outside, a);
        var external = await store.AddExternalAsync(outside, ModelKinds.Chat, EngineKinds.LlamaServer, CancellationToken.None);
        var h = new Harness(store, [ea]);

        var ex = await Assert.ThrowsAsync<ControlPipeException>(() => h.Center.StartVerifyAsync(external.Id, CancellationToken.None));
        Assert.Equal(ErrorCodes.InvalidRequest, ex.Code);
        var nope = await Assert.ThrowsAsync<ControlPipeException>(() => h.Center.StartVerifyAsync("nope", CancellationToken.None));
        Assert.Equal(ErrorCodes.NotFound, nope.Code);

        h.OnPush = e =>
        {
            if (e.State == StoreStates.Running)
            {
                h.Center.CancelOperation();
            }

            return Task.CompletedTask;
        };
        await await h.Center.StartVerifyAsync("model-a", CancellationToken.None);
        Assert.Equal(StoreStates.Cancelled, h.Last.State);
        Assert.Null(h.Notes.DamagedReason("model-a"));
        Assert.True(File.Exists(await PathOfAsync(store, "model-a")));
        Assert.True(File.Exists(outside));
    }

    // ---- through the real host and pipe ----------------------------------------------------------

    private sealed class SlowStopSupervisor(FakeEngineSupervisor inner) : IEngineSupervisor
    {
        public TaskCompletionSource StopGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Kind => inner.Kind;
        public EngineState State => inner.State;
        public EngineLaunchSpec? Current => inner.Current;
        public event EventHandler<EngineStateChange>? StateChanged
        {
            add => inner.StateChanged += value;
            remove => inner.StateChanged -= value;
        }

        public Task StartAsync(EngineLaunchSpec spec, CancellationToken ct) => inner.StartAsync(spec, ct);

        public async Task StopAsync(CancellationToken ct)
        {
            await StopGate.Task.WaitAsync(ct).ConfigureAwait(false);
            await inner.StopAsync(ct).ConfigureAwait(false);
        }

        public Task<ChatCompletionResponse> ChatAsync(ChatCompletionRequest r, CancellationToken ct) => inner.ChatAsync(r, ct);
        public IAsyncEnumerable<ChatCompletionChunk> StreamChatAsync(ChatCompletionRequest r, CancellationToken ct) => inner.StreamChatAsync(r, ct);
        public Task<EmbeddingsResponse> EmbedAsync(EmbeddingsRequest r, CancellationToken ct) => inner.EmbedAsync(r, ct);
        public Task<int> CountTokensAsync(TokenizeRequest r, CancellationToken ct) => inner.CountTokensAsync(r, ct);

        public ValueTask DisposeAsync()
        {
            StopGate.TrySetResult();
            return inner.DisposeAsync();
        }
    }

    private static async Task<HostShell> StartHostAsync(TestRoot root, ModelStore store, IReadOnlyList<CatalogueEntry> catalogue, IEngineSupervisor? chat = null) =>
        await HostShell.StartAsync(new HostShellOptions
        {
            Paths = HostPaths.Resolve(n => n == HostPaths.TestRootVariable ? root.Path : null)!,
            Supervisors = new Dictionary<string, IEngineSupervisor>
            {
                [ModelKinds.Chat] = chat ?? new FakeEngineSupervisor(ModelKinds.Chat),
                [ModelKinds.Embedding] = new FakeEngineSupervisor(ModelKinds.Embedding),
            },
            KeyProtector = new TestKeyProtector(),
            CallerResolver = new SameUserResolver(),
            QuitGrace = TimeSpan.FromMilliseconds(200),
            Store = store,
            Catalogue = catalogue,
        }) ?? throw new InvalidOperationException("pipe already owned");

    private static Task SendAsync<T>(ControlPipeClient c, string type, T payload, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        c.ExchangeAsync(type, JsonSerializer.SerializeToElement(payload, info));

    [Fact(Timeout = 30000)]
    public async Task Over_the_pipe_removing_the_model_in_use_stops_its_engine_first_then_deletes_it()
    {
        using var root = new TestRoot();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"));
        var a = RandomNumberGenerator.GetBytes(4096);
        var b = RandomNumberGenerator.GetBytes(4096);
        var ea = Entry("model-a", a);
        var eb = Entry("model-b", b);
        await InstallAsync(store, ea, a);
        await InstallAsync(store, eb, b);
        var fileA = await PathOfAsync(store, "model-a");
        var chat = new SlowStopSupervisor(new FakeEngineSupervisor(ModelKinds.Chat));
        await using var shell = await StartHostAsync(root, store, [ea, eb], chat);
        await using var client = await ControlPipeClient.ConnectAsync(shell.Paths().PipeName);
        await using var second = await ControlPipeClient.ConnectAsync(shell.Paths().PipeName);
        await client.ActivateModelAsync("model-a");
        await chat.StartAsync(new EngineLaunchSpec { Kind = ModelKinds.Chat, ModelId = "model-a", ModelFiles = [fileA] }, CancellationToken.None);
        await WaitAsync(() => store.GetAsync("model-a", CancellationToken.None).Result!.Files.All(f => f.State == StoredFileState.InUse));
        var events = new ConcurrentQueue<DownloadProgressEvent>();
        client.DownloadProgress += (_, e) => events.Enqueue(e);

        var removing = SendAsync(second, ControlTypes.RemoveModel, new RemoveModelCommand { CatalogueId = "model-a" }, AiHostJsonContext.Default.RemoveModelCommand);
        await Task.Delay(200);
        Assert.False(removing.IsCompleted); // waits for the engine to stop
        Assert.True(File.Exists(fileA));

        chat.StopGate.SetResult();
        await removing;

        Assert.False(File.Exists(fileA));
        Assert.Null(shell.Runtime.GetActive(ModelKinds.Chat)); // goes to "no model"
        Assert.Equal(EngineState.Stopped, chat.State);
        Assert.True(File.Exists(await PathOfAsync(store, "model-b")));
        var listing = await client.ListModelsAsync();
        Assert.DoesNotContain(listing.Models, m => m.Installed && m.Id == "model-a");
        Assert.DoesNotContain(listing.Models, m => m.Active);
        await WaitAsync(() => events.Any(e => e.State == "removed"));
        Assert.Contains(events, e => e.State == "removing");
    }

    [Fact(Timeout = 30000)]
    public async Task Over_the_pipe_moving_reports_progress_updates_settings_and_the_old_copies_go()
    {
        using var root = new TestRoot();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"));
        var a = RandomNumberGenerator.GetBytes(50_000);
        var ea = Entry("model-a", a);
        await InstallAsync(store, ea, a);
        var oldFile = await PathOfAsync(store, "model-a");
        await using var shell = await StartHostAsync(root, store, [ea]);
        await using var client = await ControlPipeClient.ConnectAsync(shell.Paths().PipeName);
        var events = new ConcurrentQueue<StoreProgressEvent>();
        client.StoreProgress += (_, e) => events.Enqueue(e);
        var parent = root.Sub("elsewhere");

        await client.MoveStoreAsync(parent);
        await WaitAsync(() => events.Any(e => e.State is StoreStates.Done or StoreStates.Failed));

        Assert.Equal(StoreStates.Done, events.Last().State);
        Assert.False(File.Exists(oldFile));
        Assert.Equal(ModelStore.StoreFolderIn(parent), (await client.GetStoreFolderAsync()).Path, ignoreCase: true);
        Assert.Equal(parent, shell.Settings.Current.ModelsFolder);
        Assert.True(File.Exists(Path.Combine(ModelStore.StoreFolderIn(parent), "model-a", "model-a.gguf")) ||
            Directory.EnumerateFiles(ModelStore.StoreFolderIn(parent), "model-a.gguf", SearchOption.AllDirectories).Any());
        Assert.Contains(events, e => e.Operation == StoreOperations.Move && e.State == StoreStates.Running);
        Assert.True((await client.ListModelsAsync()).Models.Single().Installed);
        // Nothing is left to cancel.
        await client.CancelStoreOperationAsync();
    }

    [Fact(Timeout = 30000)]
    public async Task Over_the_pipe_a_check_that_finds_damage_answers_with_an_event_and_the_listing_says_damaged()
    {
        using var root = new TestRoot();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"));
        var a = RandomNumberGenerator.GetBytes(80_000);
        var ea = Entry("model-a", a);
        await InstallAsync(store, ea, a);
        var file = await PathOfAsync(store, "model-a");
        var broken = (byte[])a.Clone();
        broken[10] ^= 1;
        File.WriteAllBytes(file, broken);
        await using var shell = await StartHostAsync(root, store, [ea]);
        await using var client = await ControlPipeClient.ConnectAsync(shell.Paths().PipeName);
        var events = new ConcurrentQueue<StoreProgressEvent>();
        client.StoreProgress += (_, e) => events.Enqueue(e);

        await client.VerifyModelAsync("model-a");
        await WaitAsync(() => events.Any(e => e.State == StoreStates.Damaged));

        Assert.True(File.Exists(file));
        Assert.Equal(broken, File.ReadAllBytes(file));
        Assert.True((await client.ListModelsAsync()).Models.Single().Damaged);
        Assert.True(File.Exists(Path.Combine(shell.Paths().DataRoot, "model-notes.json")));
    }
}
