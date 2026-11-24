using System.Diagnostics;
using System.Security.Cryptography;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Store;
using Portal.AiHost.Platform.Windows.Pipes;
using PortalAIHost.Service;

namespace Portal.AiHost.Server.Tests;

/// <summary>Leftover old copies, pending moves, the operation guard, damaged removal and canonical paths.</summary>
public sealed partial class ModelManagementTests
{
    private static void Junction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    // ---- 1 and 2: a previous move that is not finished ---------------------------------------------

    [Fact(Timeout = 30000)]
    public async Task A_previous_store_whose_folder_is_gone_is_forgotten_and_a_new_move_works()
    {
        var (root, store, h, fileA, fileB, oldRoot, a, b) = await TwoModelsAsync();
        using var _r = root;
        using var _s = store;
        await store.MoveStoreAsync(root.Sub("d1"), null, CancellationToken.None); // switched; old copies still there
        Assert.NotNull(store.PreviousLocation);
        Assert.False(await store.ClearStalePreviousStoreAsync(CancellationToken.None)); // old copies exist: not stale
        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => store.MoveStoreAsync(root.Sub("d2"), null, CancellationToken.None));
        Assert.Contains(oldRoot, blocked.Message); // names the folder and what to do
        Assert.Contains("try again", blocked.Message);

        Directory.Delete(oldRoot, recursive: true); // the user deleted the old folder themselves (test cleanup of its own temp folder)
        Assert.True(await store.ClearStalePreviousStoreAsync(CancellationToken.None));
        Assert.Null(store.PreviousLocation);
        await store.MoveStoreAsync(root.Sub("d2"), null, CancellationToken.None);
        Assert.Equal(ModelStore.StoreFolderIn(root.Path + "\\d2"), store.Location.Path, ignoreCase: true);
    }

    [Fact(Timeout = 30000)]
    public async Task A_new_move_first_finishes_the_old_copies_of_the_last_one_and_forgets_that_folder()
    {
        var (root, store, h, fileA, fileB, oldRoot, a, b) = await TwoModelsAsync();
        using var _r = root;
        using var _s = store;
        await store.MoveStoreAsync(root.Sub("d1"), null, CancellationToken.None);

        await await h.Center.StartMoveAsync(root.Sub("d2"), CancellationToken.None);

        Assert.Equal(StoreStates.Done, h.Last.State);
        Assert.False(File.Exists(fileA));
        Assert.False(File.Exists(fileB));
        Assert.Contains(oldRoot, h.Forgotten, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(a, File.ReadAllBytes(await PathOfAsync(store, "model-a")));
    }

    [Fact(Timeout = 30000)]
    public async Task Old_copies_that_cannot_be_removed_keep_the_folder_listed_and_a_new_move_names_it()
    {
        var (root, store, h, fileA, fileB, oldRoot, a, b) = await TwoModelsAsync();
        using var _r = root;
        using var _s = store;
        await store.MoveStoreAsync(root.Sub("d1"), null, CancellationToken.None);
        FileStream? locked = new(fileA, FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            var ex = await Assert.ThrowsAsync<ControlPipeException>(() => h.Center.StartMoveAsync(root.Sub("d2"), CancellationToken.None));
            Assert.Contains(oldRoot, ex.Message);
            Assert.Contains(oldRoot, h.Remembered, StringComparer.OrdinalIgnoreCase); // stays listed under earlier folders
            Assert.DoesNotContain(oldRoot, h.Forgotten, StringComparer.OrdinalIgnoreCase);
            Assert.False(File.Exists(fileB)); // the file that could be removed was
        }
        finally
        {
            locked.Dispose();
            locked = null;
        }

        await await h.Center.StartMoveAsync(root.Sub("d2"), CancellationToken.None); // once the file is free it finishes
        Assert.Equal(StoreStates.Done, h.Last.State);
        Assert.False(File.Exists(fileA));
    }

    [Fact(Timeout = 30000)]
    public async Task One_locked_old_file_does_not_stop_the_others_and_the_old_inventory_never_lists_deleted_files()
    {
        var (root, store, h, fileA, fileB, oldRoot, a, b) = await TwoModelsAsync();
        using var _r = root;
        using var _s = store;
        await store.MoveStoreAsync(root.Sub("d1"), null, CancellationToken.None);
        using (new FileStream(fileA, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var left = await store.RemoveOldCopiesAsync(CancellationToken.None);
            Assert.Equal([fileA], left, StringComparer.OrdinalIgnoreCase);
        }

        Assert.True(File.Exists(fileA));
        Assert.False(File.Exists(fileB));
        using var oldStore = ModelStore.Open(oldRoot);
        var listed = await oldStore.ListAsync(CancellationToken.None);
        Assert.Equal(["model-a"], listed.Select(m => m.Id)); // model-b's deleted file is not listed any more
        Assert.NotNull(store.PreviousLocation); // still pending
    }

    // ---- 3: the pending move is persisted -------------------------------------------------------------

    [Fact(Timeout = 30000)]
    public async Task The_target_folder_is_remembered_before_anything_is_copied_and_settings_failures_are_retried_once()
    {
        var (root, store, h, fileA, fileB, oldRoot, a, b) = await TwoModelsAsync();
        using var _r = root;
        using var _s = store;
        var parent = root.Sub("d1");
        h.ApplyFailures = 1;
        h.OnPush = e =>
        {
            if (e.State == StoreStates.Running && e.Fraction > 0)
            {
                Assert.Contains(ModelStore.StoreFolderIn(parent), h.Remembered, StringComparer.OrdinalIgnoreCase);
            }

            return Task.CompletedTask;
        };

        await await h.Center.StartMoveAsync(parent, CancellationToken.None);

        Assert.Equal(StoreStates.Done, h.Last.State);
        Assert.Equal(2, h.ApplyCalls);
        Assert.NotNull(h.Moved);
        Assert.False(File.Exists(fileA));
        Assert.Equal("Your models are being moved. Try again in a minute.", h.Maintenance.First());
        Assert.Null(h.Maintenance.Last()); // requests are allowed again
    }

    [Fact(Timeout = 30000)]
    public async Task If_settings_cannot_be_saved_the_old_copies_are_kept_and_the_message_says_so()
    {
        var (root, store, h, fileA, fileB, oldRoot, a, b) = await TwoModelsAsync();
        using var _r = root;
        using var _s = store;
        h.ApplyFailures = 5;

        await await h.Center.StartMoveAsync(root.Sub("d1"), CancellationToken.None);

        Assert.Equal(StoreStates.Done, h.Last.State);
        Assert.Contains("still point at the old folder", h.Last.Message);
        Assert.Equal(2, h.ApplyCalls);
        Assert.Equal(a, File.ReadAllBytes(fileA)); // old copies untouched
        Assert.Equal(b, File.ReadAllBytes(fileB));
        Assert.Equal(a, File.ReadAllBytes(await PathOfAsync(store, "model-a"))); // and the new ones verified
        Assert.NotNull(store.PreviousLocation);
    }

    [Fact(Timeout = 30000)]
    public async Task A_cancelled_move_that_cleaned_up_forgets_the_target_folder()
    {
        var (root, store, h, fileA, fileB, oldRoot, a, b) = await TwoModelsAsync();
        using var _r = root;
        using var _s = store;
        var parent = root.Sub("d1");
        h.OnPush = e =>
        {
            if (e is { State: StoreStates.Running, Fraction: > 0 and < 1 })
            {
                h.Center.CancelOperation();
            }

            return Task.CompletedTask;
        };

        await await h.Center.StartMoveAsync(parent, CancellationToken.None);

        var target = ModelStore.StoreFolderIn(parent);
        Assert.Equal(StoreStates.Cancelled, h.Last.State);
        Assert.Contains(target, h.Remembered, StringComparer.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(target));
        Assert.Contains(target, h.Forgotten, StringComparer.OrdinalIgnoreCase);
    }

    // ---- 4: leftovers of a move that could not clean up -----------------------------------------------

    [Fact(Timeout = 30000)]
    public async Task A_move_that_cannot_clean_up_says_partly_cleaned_up_and_the_leftovers_are_removable_through_the_store()
    {
        var (root, store, h, fileA, fileB, oldRoot, a, b) = await TwoModelsAsync();
        using var _r = root;
        using var _s = store;
        var parent = root.Sub("d1");
        var target = ModelStore.StoreFolderIn(parent);
        FileStream? held = null;
        h.OnPush = e =>
        {
            if (held is null && e is { State: StoreStates.Running, Fraction: > 0 and < 1 })
            {
                var copied = Directory.EnumerateFiles(target, "*.gguf", SearchOption.AllDirectories).First();
                held = new FileStream(copied, FileMode.Open, FileAccess.Read, FileShare.None); // cleanup cannot delete it
                h.Center.CancelOperation();
            }

            return Task.CompletedTask;
        };

        await await h.Center.StartMoveAsync(parent, CancellationToken.None);

        Assert.Equal(StoreStates.Failed, h.Last.State);
        Assert.Contains("partly cleaned up", h.Last.Message);
        Assert.Contains(target, h.Remembered, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(target, h.Forgotten, StringComparer.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(target));
        AssertOldStoreIntact(oldRoot, fileA, fileB, a, b);
        Assert.Equal(oldRoot, store.Location.Path, ignoreCase: true);

        held!.Dispose();
        // At the next start the folder is listed as an earlier folder; its leftovers are listed by the store and removed by it.
        using var leftover = ModelStore.Open(target);
        var models = await leftover.ListAsync(CancellationToken.None);
        Assert.NotEmpty(models);
        foreach (var m in models)
        {
            await leftover.RemoveAsync(m.Id, CancellationToken.None);
        }

        Assert.Empty(Directory.EnumerateFiles(target, "*.gguf*", SearchOption.AllDirectories));
        Assert.Empty(await leftover.ListAsync(CancellationToken.None));
    }

    // ---- 5: the operation guard ------------------------------------------------------------------------

    [Fact(Timeout = 30000)]
    public async Task While_a_move_runs_remove_download_and_removing_from_an_earlier_folder_are_refused()
    {
        var (root, store, h, fileA, fileB, oldRoot, a, b) = await TwoModelsAsync();
        using var _r = root;
        using var _s = store;
        var gate = new TaskCompletionSource();
        h.OnStop = async ct => await gate.Task.WaitAsync(ct);
        var running = await h.Center.StartMoveAsync(root.Sub("d1"), CancellationToken.None);

        await Assert.ThrowsAsync<ControlPipeException>(() => h.Center.RemoveAsync("model-a", CancellationToken.None));
        await Assert.ThrowsAsync<ControlPipeException>(() => h.Center.StartAsync("model-a"));
        await Assert.ThrowsAsync<ControlPipeException>(() => h.Center.RemoveFromPreviousAsync("model-a", oldRoot, CancellationToken.None));
        Assert.Throws<ControlPipeException>(() => h.Center.AssertIdle());
        Assert.True(File.Exists(fileA));

        gate.SetResult();
        await running;
        h.Center.AssertIdle(); // free again
    }

    [Fact(Timeout = 30000)]
    public async Task A_check_blocks_removal_and_a_model_removed_meanwhile_is_not_marked_damaged()
    {
        var (root, store) = NewStore();
        using var _r = root;
        using var _s = store;
        var a = RandomNumberGenerator.GetBytes(40_000);
        var ea = Entry("model-a", a);
        await InstallAsync(store, ea, a);
        var file = await PathOfAsync(store, "model-a");
        var broken = (byte[])a.Clone();
        broken[5] ^= 1;
        File.WriteAllBytes(file, broken);
        var h = new Harness(store, [ea]);
        h.OnPush = async e =>
        {
            if (e.State == StoreStates.Running && e.CatalogueId == "model-a" && e.Fraction == 0)
            {
                await Assert.ThrowsAsync<ControlPipeException>(() => h.Center.RemoveAsync("model-a", CancellationToken.None));
                await store.RemoveAsync("model-a", CancellationToken.None); // gone by another path before the check ends
            }
        };

        await await h.Center.StartVerifyAsync("model-a", CancellationToken.None);

        Assert.Null(h.Notes.DamagedReason("model-a"));
        Assert.NotEqual(StoreStates.Damaged, h.Last.State);
    }

    [Fact(Timeout = 30000)]
    public async Task Requests_that_need_a_model_fail_cleanly_while_the_models_are_being_moved()
    {
        using var root = new TestRoot();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"));
        var a = RandomNumberGenerator.GetBytes(4096);
        var ea = Entry("model-a", a);
        await InstallAsync(store, ea, a);
        await using var shell = await StartHostAsync(root, store, [ea]);
        await using var client = await ControlPipeClient.ConnectAsync(shell.Paths().PipeName);
        await client.ActivateModelAsync("model-a");

        shell.Runtime.MaintenanceMessage = "Your models are being moved. Try again in a minute.";
        var ex = await Assert.ThrowsAsync<ApiException>(() => shell.Runtime.EnsureLoadedAsync(ModelKinds.Chat, CancellationToken.None));
        Assert.Equal(ErrorCodes.EngineRestarting, ex.Code);
        await shell.Runtime.UnloadAsync(ModelKinds.Chat, CancellationToken.None); // stopping engines is still allowed
        shell.Runtime.MaintenanceMessage = null;
        await shell.Runtime.EnsureLoadedAsync(ModelKinds.Chat, CancellationToken.None);
    }

    // ---- 6 and 8: damaged removal and a failed removal of the model in use --------------------------------

    [Fact(Timeout = 30000)]
    public async Task A_truncated_damaged_model_is_removed_only_after_the_damaged_flag_and_only_its_own_files()
    {
        var (root, store) = NewStore();
        using var _r = root;
        using var _s = store;
        var a = RandomNumberGenerator.GetBytes(40_000);
        var ea = Entry("model-a", a);
        await InstallAsync(store, ea, a);
        var file = await PathOfAsync(store, "model-a");
        var stray = Path.Combine(Path.GetDirectoryName(file)!, "not-ours.txt");
        File.WriteAllText(stray, "user file");
        File.WriteAllBytes(file, a[..100]); // truncated: the size no longer matches what the store recorded
        var h = new Harness(store, [ea]);

        // Not marked damaged: the flag alone changes nothing, the store refuses.
        await Assert.ThrowsAsync<ControlPipeException>(() => h.Center.RemoveAsync("model-a", CancellationToken.None, removeDamaged: true));
        Assert.True(File.Exists(file));
        // Not confirmed as damaged-removal: refused too.
        h.Notes.MarkDamaged("model-a", "model-a.gguf does not match the model list.");
        await Assert.ThrowsAsync<ControlPipeException>(() => h.Center.RemoveAsync("model-a", CancellationToken.None));
        Assert.True(File.Exists(file));

        await h.Center.RemoveAsync("model-a", CancellationToken.None, removeDamaged: true);

        Assert.False(File.Exists(file));
        Assert.Null(await store.GetAsync("model-a", CancellationToken.None));
        Assert.True(File.Exists(stray)); // not listed by the store: never touched
        Assert.Null(h.Notes.DamagedReason("model-a"));
    }

    [Fact(Timeout = 30000)]
    public async Task A_removal_that_fails_after_the_active_model_was_set_aside_puts_it_back()
    {
        var (root, store) = NewStore();
        using var _r = root;
        using var _s = store;
        var a = RandomNumberGenerator.GetBytes(40_000);
        var ea = Entry("model-a", a);
        await InstallAsync(store, ea, a);
        var file = await PathOfAsync(store, "model-a");
        File.WriteAllBytes(file, a[..100]); // the store refuses: size changed
        var h = new Harness(store, [ea]);
        h.Active[ModelKinds.Chat] = "model-a";

        await Assert.ThrowsAsync<ControlPipeException>(() => h.Center.RemoveAsync("model-a", CancellationToken.None));

        Assert.Equal(["clear:chat", "activate:model-a"], h.Calls.ToArray());
        Assert.Equal("model-a", h.Active[ModelKinds.Chat]);
        Assert.True(File.Exists(file));
        Assert.Equal("remove_failed", h.Downloads.Last().State);
    }

    // ---- 9: canonical destination ---------------------------------------------------------------------------

    [Fact(Timeout = 30000)]
    public async Task A_destination_that_is_a_link_or_resolves_into_the_current_store_is_refused()
    {
        var (root, store) = NewStore();
        using var _r = root;
        using var _s = store;
        var outside = root.Sub("real-outside");
        var link = Path.Combine(root.Path, "link-outside");
        Junction(link, outside);
        var viaJunction = Path.Combine(root.Path, "to-store");
        try
        {
        Assert.Contains("link", store.CheckMoveTarget(link));

        var inside = Path.Combine(store.Location.Path, "sub");
        Directory.CreateDirectory(inside);
        Junction(viaJunction, store.Location.Path);
        // Not a link itself (a real folder reached through a link) but its real path is inside the current store.
        var reached = Path.Combine(viaJunction, "sub");
        Assert.Contains("inside the current models folder", store.CheckMoveTarget(reached));
        Assert.Contains("link", store.CheckMoveTarget(viaJunction));
        Assert.Null(store.CheckMoveTarget(outside));
        await Assert.ThrowsAsync<StoreRefusedException>(() => store.MoveStoreAsync(viaJunction, null, CancellationToken.None));
        Assert.True(Directory.Exists(store.Location.Path));
        }
        finally
        {
            // Junctions are removed first (non-recursive: only the link goes), so cleanup never walks into their targets.
            foreach (var j in new[] { link, viaJunction })
            {
                if (Directory.Exists(j)) { Directory.Delete(j, recursive: false); }
            }
        }
    }

    [Fact(Timeout = 30000)]
    public async Task Chat_and_embeddings_requests_over_http_fail_cleanly_during_a_move_and_never_load_an_engine()
    {
        await using var host = await TestHost.StartAsync();
        var token = await host.GetTokenAsync();
        host.Runtime.MaintenanceMessage = "Your models are being moved. Try again in a minute.";

        foreach (var (route, body) in new[]
        {
            (Routes.ChatCompletions, """{ "messages": [ { "role": "user", "content": "Hi" } ] }"""),
            (Routes.Embeddings, """{ "input": "hello" }"""),
        })
        {
            using var response = await host.Http.SendAsync(host.Authed(HttpMethod.Post, route, token, body));
            Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var error = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
            Assert.Equal(ErrorCodes.EngineRestarting, error.GetProperty("code").GetString());
            Assert.Contains("being moved", error.GetProperty("message").GetString());
        }

        Assert.Equal(EngineState.Stopped, host.Chat.State);
        Assert.Equal(EngineState.Stopped, host.Embed.State);
    }

    [Fact(Timeout = 30000)]
    public async Task Choosing_a_model_during_a_move_or_a_check_is_refused()
    {
        using var root = new TestRoot();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"));
        var a = RandomNumberGenerator.GetBytes(96_000_000); // big enough that the check is still running
        var ea = Entry("model-a", a);
        var eb = Entry("model-b", RandomNumberGenerator.GetBytes(1000));
        await InstallAsync(store, ea, a);
        await using var shell = await StartHostAsync(root, store, [ea, eb]);

        // A check is running: hold it with a slow hash by cancelling only after the assertion.
        var running = await shell.Models.StartVerifyAsync("model-a", CancellationToken.None);
        var during = await Record.ExceptionAsync(() => shell.ActivateAsync("model-a", CancellationToken.None));
        shell.Models.CancelOperation();
        await running;
        Assert.IsType<ControlPipeException>(during);
        Assert.Equal(ErrorCodes.InvalidRequest, ((ControlPipeException)during).Code);
        Assert.Null(shell.Runtime.GetActive(ModelKinds.Chat));

        await shell.ActivateAsync("model-a", CancellationToken.None); // idle again: allowed
        Assert.Equal("model-a", shell.Runtime.GetActive(ModelKinds.Chat)?.ModelId);
    }
}
