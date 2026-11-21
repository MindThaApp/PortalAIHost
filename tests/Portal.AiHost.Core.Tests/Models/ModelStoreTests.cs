using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Store;

namespace Portal.AiHost.Core.Tests.Models;

/// <summary>Destructive-code gate: every delete, truncate, replace and move path in the store.</summary>
public class ModelStoreTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static async Task<StoredFile> AddFileAsync(ModelStore store, string modelId, string name, byte[] data)
    {
        var part = await store.BeginFileAsync(modelId, name, Ct);
        await File.WriteAllBytesAsync(part, data, Ct);
        return await store.CommitFileAsync(modelId, part, TestData.Sha(data), Ct);
    }

    private static string StoreJson(ModelStore s) => Path.Combine(s.Location.Path, "store.json");

    [Fact]
    public void NewStoreCarriesMarker()
    {
        using var root = new TestRoot();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        Assert.True(File.Exists(Path.Combine(store.Location.Path, ".portal-ai-host-store")));
        Assert.NotEqual(Guid.Empty, store.Location.StoreId);

        var reopened = ModelStore.Open(store.Location.Path, new FakeDisk());
        Assert.Equal(store.Location.StoreId, reopened.Location.StoreId);
    }

    [Fact]
    public void UnmarkedFolderIsRefusedAndUntouched()
    {
        using var root = new TestRoot();
        var folder = root.Sub("existing");
        var mine = Path.Combine(folder, "my-notes.txt");
        File.WriteAllText(mine, "keep");

        Assert.Throws<StoreRefusedException>(() => ModelStore.Open(folder, new FakeDisk()));
        Assert.Throws<StoreRefusedException>(() => ModelStore.OpenOrCreate(folder, new FakeDisk()));
        Assert.Equal(["my-notes.txt"], Directory.GetFileSystemEntries(folder).Select(Path.GetFileName));
        Assert.Equal("keep", File.ReadAllText(mine));
    }

    [Fact]
    public void UserLocationGetsItsOwnSubfolderNeverAnExistingOne()
    {
        using var root = new TestRoot();
        var parent = root.Sub("D-drive");
        Directory.CreateDirectory(Path.Combine(parent, ModelStore.UserFolderName));
        File.WriteAllText(Path.Combine(parent, ModelStore.UserFolderName, "foreign.gguf"), "x");

        var store = ModelStore.CreateInParent(parent, new FakeDisk());
        Assert.Equal(Path.Combine(parent, ModelStore.UserFolderName + " (2)"), store.Location.Path);
        Assert.True(File.Exists(Path.Combine(parent, ModelStore.UserFolderName, "foreign.gguf")));
    }

    [Fact]
    public void Fat32IsRefused()
    {
        using var root = new TestRoot();
        Assert.Throws<StoreRefusedException>(() => ModelStore.CreateInParent(root.Sub("usb"), new FakeDisk(fileSystem: "FAT32")));
        Assert.False(Directory.Exists(Path.Combine(root.Path, "usb", ModelStore.UserFolderName)));
        _ = ModelStore.CreateInParent(root.Sub("exfat"), new FakeDisk(fileSystem: "exFAT"));
    }

    [Fact]
    public async Task RemoveDeletesOwnFilesAndEmptyFolder()
    {
        using var root = new TestRoot();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        var f = await AddFileAsync(store, "tiny", "tiny-00001-of-00002.gguf", TestData.RandomBytes(1000, 1));
        await AddFileAsync(store, "tiny", "tiny-00002-of-00002.gguf", TestData.RandomBytes(1000, 2));
        Assert.True(File.Exists(f.Path));
        Assert.Equal("tiny-00001-of-00002.gguf", Path.GetFileName(f.Path));

        await store.RemoveAsync("tiny", Ct);
        Assert.False(File.Exists(f.Path));
        Assert.False(Directory.Exists(Path.GetDirectoryName(f.Path)));
        Assert.Null(await store.GetAsync("tiny", Ct));
    }

    [Fact]
    public async Task RemoveLeavesAFileThatWasReplacedBySameSizeFile()
    {
        using var root = new TestRoot();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        var f = await AddFileAsync(store, "tiny", "tiny.gguf", TestData.RandomBytes(500, 4));
        // Another program swaps the file for its own file of the same size (a new file ID).
        var swap = Path.Combine(root.Path, "swap.bin");
        File.WriteAllBytes(swap, TestData.RandomBytes(500, 5));
        File.Delete(f.Path);
        File.Move(swap, f.Path);
        var theirs = File.ReadAllBytes(f.Path);

        await Assert.ThrowsAsync<StoreSafetyException>(() => store.RemoveAsync("tiny", Ct));
        Assert.Equal(theirs, File.ReadAllBytes(f.Path));
        Assert.NotNull(await store.GetAsync("tiny", Ct));
    }

    [Fact]
    public async Task ForeignFileInModelFolderSurvivesRemove()
    {
        using var root = new TestRoot();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        var f = await AddFileAsync(store, "tiny", "tiny.gguf", TestData.RandomBytes(500, 3));
        var foreign = Path.Combine(Path.GetDirectoryName(f.Path)!, "users-own.gguf");
        File.WriteAllText(foreign, "precious");
        var foreignInRoot = Path.Combine(store.Location.Path, "photo.jpg");
        File.WriteAllText(foreignInRoot, "precious");

        await store.RemoveAsync("tiny", Ct);
        Assert.False(File.Exists(f.Path));
        Assert.Equal("precious", File.ReadAllText(foreign));
        Assert.Equal("precious", File.ReadAllText(foreignInRoot));
    }

    [Fact]
    public async Task NameClashStopsWithoutRenamingAndForeignFileIsNeverReplaced()
    {
        using var root = new TestRoot();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        var part = await store.BeginFileAsync("m", "a.gguf", Ct);
        var folder = Path.GetDirectoryName(part)!;
        await store.DiscardPartAsync("m", part, Ct);

        // A foreign file sits where the final file would go.
        Directory.CreateDirectory(folder);
        var foreign = Path.Combine(folder, "a.gguf");
        File.WriteAllText(foreign, "not the host's");

        await Assert.ThrowsAsync<StoreSafetyException>(() => store.BeginFileAsync("m", "a.gguf", Ct));
        Assert.Equal("not the host's", File.ReadAllText(foreign));
        Assert.DoesNotContain(Directory.GetFiles(folder), f => f.Contains('(', StringComparison.Ordinal));
    }

    [Fact]
    public async Task FileAppearingAtFinalNameBeforeCommitIsKept()
    {
        using var root = new TestRoot();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        var data = TestData.RandomBytes(100, 5);
        var part = await store.BeginFileAsync("m", "b.gguf", Ct);
        await File.WriteAllBytesAsync(part, data, Ct);
        var intruder = Path.Combine(Path.GetDirectoryName(part)!, "b.gguf");
        File.WriteAllText(intruder, "appeared meanwhile");

        await Assert.ThrowsAsync<StoreSafetyException>(() => store.CommitFileAsync("m", part, TestData.Sha(data), Ct));
        Assert.Equal("appeared meanwhile", File.ReadAllText(intruder));
        Assert.Equal(data, File.ReadAllBytes(part));
        Assert.Equal(StoredFileState.Partial, (await store.GetAsync("m", Ct))!.Files[0].State);
    }

    [Theory]
    [InlineData("..\\evil.gguf")]
    [InlineData("../evil.gguf")]
    [InlineData("sub\\evil.gguf")]
    [InlineData("C:\\Windows\\evil.gguf")]
    [InlineData("store.json")]
    public async Task UnsafeFileNamesAreRefused(string name)
    {
        using var root = new TestRoot();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        await Assert.ThrowsAsync<StoreSafetyException>(() => store.BeginFileAsync("m", name, Ct));
        await Assert.ThrowsAsync<StoreSafetyException>(() => store.BeginFileAsync("..", "ok.gguf", Ct));
    }

    [Fact]
    public async Task TamperedManifestPathOutsideStoreIsRefused()
    {
        using var root = new TestRoot();
        var victim = Path.Combine(root.Sub("outside"), "victim.gguf");
        File.WriteAllBytes(victim, new byte[100]);
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        await AddFileAsync(store, "m", "victim.gguf", new byte[100]);

        var json = File.ReadAllText(StoreJson(store));
        File.WriteAllText(StoreJson(store), json.Replace("\"path\": \"m\\\\victim.gguf\"", "\"path\": \"..\\\\outside\\\\victim.gguf\""));
        var reopened = ModelStore.Open(store.Location.Path, new FakeDisk());

        Assert.Contains("outside", File.ReadAllText(StoreJson(store)), StringComparison.Ordinal);
        Assert.Equal(victim, Path.GetFullPath(Path.Combine(store.Location.Path, "..", "outside", "victim.gguf")));
        await Assert.ThrowsAsync<StoreSafetyException>(() => reopened.RemoveAsync("m", Ct));
        Assert.True(File.Exists(victim));
    }

    [Fact]
    public async Task JunctionEscapeIsRefused()
    {
        using var root = new TestRoot();
        var outside = root.Sub("outside");
        var victim = Path.Combine(outside, "victim.gguf");
        File.WriteAllBytes(victim, new byte[100]);
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        var own = await AddFileAsync(store, "m", "victim.gguf", new byte[100]);
        var modelFolder = Path.GetDirectoryName(own.Path)!;
        TestRoot.CreateJunction(Path.Combine(modelFolder, "link"), outside);

        var json = File.ReadAllText(StoreJson(store));
        File.WriteAllText(StoreJson(store), json.Replace("\"path\": \"m\\\\victim.gguf\"", "\"path\": \"m\\\\link\\\\victim.gguf\""));
        var reopened = ModelStore.Open(store.Location.Path, new FakeDisk());

        await Assert.ThrowsAsync<StoreSafetyException>(() => reopened.RemoveAsync("m", Ct));
        Assert.True(File.Exists(victim));
        Assert.True(File.Exists(own.Path));
    }

    [Fact]
    public async Task ModelFolderReplacedByJunctionIsRefused()
    {
        using var root = new TestRoot();
        var outside = root.Sub("outside");
        var victim = Path.Combine(outside, "a.gguf");
        File.WriteAllBytes(victim, new byte[50]);
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        var own = await AddFileAsync(store, "m", "a.gguf", new byte[50]);
        var modelFolder = Path.GetDirectoryName(own.Path)!;
        File.Delete(own.Path);
        Directory.Delete(modelFolder);
        TestRoot.CreateJunction(modelFolder, outside);

        await Assert.ThrowsAsync<StoreSafetyException>(() => store.RemoveAsync("m", Ct));
        Assert.True(File.Exists(victim));
    }

    [Fact]
    public async Task SymlinkedFileIsRefused()
    {
        using var root = new TestRoot();
        var victim = Path.Combine(root.Sub("outside"), "v.gguf");
        File.WriteAllBytes(victim, new byte[10]);
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        var own = await AddFileAsync(store, "m", "a.gguf", new byte[10]);
        File.Delete(own.Path);
        try
        {
            File.CreateSymbolicLink(own.Path, victim);
        }
        catch (IOException)
        {
            Assert.Skip("Symbolic links need developer mode or elevation on this machine.");
        }
        catch (UnauthorizedAccessException)
        {
            Assert.Skip("Symbolic links need developer mode or elevation on this machine.");
        }

        await Assert.ThrowsAsync<StoreSafetyException>(() => store.RemoveAsync("m", Ct));
        Assert.True(File.Exists(victim));
    }

    [Fact]
    public async Task MissingOrChangedMarkerBlocksDeletes()
    {
        using var root = new TestRoot();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        var own = await AddFileAsync(store, "m", "a.gguf", new byte[10]);
        var marker = Path.Combine(store.Location.Path, ".portal-ai-host-store");
        var original = File.ReadAllText(marker);

        File.WriteAllText(marker, original.Replace(store.Location.StoreId.ToString(), Guid.NewGuid().ToString()));
        await Assert.ThrowsAsync<StoreSafetyException>(() => store.RemoveAsync("m", Ct));
        File.Delete(marker);
        await Assert.ThrowsAsync<StoreSafetyException>(() => store.RemoveAsync("m", Ct));
        Assert.True(File.Exists(own.Path));
    }

    [Fact]
    public async Task FileChangedByUserIsNotDeletedAndNothingElseIsEither()
    {
        using var root = new TestRoot();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        var a = await AddFileAsync(store, "m", "m-00001-of-00002.gguf", new byte[10]);
        var b = await AddFileAsync(store, "m", "m-00002-of-00002.gguf", new byte[10]);
        File.WriteAllText(b.Path, "the user's replacement, longer than ten bytes");

        await Assert.ThrowsAsync<StoreSafetyException>(() => store.RemoveAsync("m", Ct));
        Assert.True(File.Exists(a.Path));
        Assert.True(File.Exists(b.Path));
    }

    [Fact]
    public async Task ExternalModelIsOnlyForgotten()
    {
        using var root = new TestRoot();
        var users = Path.Combine(root.Sub("Downloads"), "my-model.gguf");
        File.WriteAllBytes(users, new byte[64]);
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());

        var ext = await store.AddExternalAsync(users, "chat", "llama-server", Ct);
        Assert.Equal(ModelOrigin.External, ext.Origin);
        Assert.Equal(users, ext.Files[0].Path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.BeginFileAsync(ext.Id, "x.gguf", Ct));

        await store.RemoveAsync(ext.Id, Ct);
        Assert.True(File.Exists(users));
        Assert.Null(await store.GetAsync(ext.Id, Ct));
    }

    [Fact]
    public async Task FilesInsideTheStoreCannotBeAddedAsExternal()
    {
        using var root = new TestRoot();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        var own = await AddFileAsync(store, "m", "a.gguf", new byte[10]);
        await Assert.ThrowsAsync<StoreSafetyException>(() => store.AddExternalAsync(own.Path, "chat", "llama-server", Ct));
    }

    [Fact]
    public async Task InUseModelIsNotRemoved()
    {
        using var root = new TestRoot();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        var own = await AddFileAsync(store, "m", "a.gguf", new byte[10]);
        await store.SetInUseAsync("m", true, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RemoveAsync("m", Ct));
        Assert.True(File.Exists(own.Path));
        await store.SetInUseAsync("m", false, Ct);
        await store.RemoveAsync("m", Ct);
        Assert.False(File.Exists(own.Path));
    }

    [Fact]
    public async Task DiscardAndResetOnlyTouchTheRecordedPart()
    {
        using var root = new TestRoot();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        var part = await store.BeginFileAsync("m", "a.gguf", Ct);
        await File.WriteAllBytesAsync(part, new byte[100], Ct);
        await store.WriteResumeStateAsync("m", part, "{}", Ct);
        var neighbour = Path.Combine(Path.GetDirectoryName(part)!, "neighbour.gguf.part");
        File.WriteAllText(neighbour, "foreign");

        await Assert.ThrowsAsync<StoreSafetyException>(() => store.ResetPartAsync("m", neighbour, Ct));
        await Assert.ThrowsAsync<StoreSafetyException>(() => store.DiscardPartAsync("m", neighbour, Ct));

        await store.ResetPartAsync("m", part, Ct);
        Assert.Equal(0, new FileInfo(part).Length);
        Assert.False(File.Exists(part + ".json"));

        await store.DiscardPartAsync("m", part, Ct);
        Assert.False(File.Exists(part));
        Assert.Equal("foreign", File.ReadAllText(neighbour));
    }

    [Fact]
    public async Task MoveCopiesVerifiesSwitchesAndKeepsOldCopiesUntilAsked()
    {
        using var root = new TestRoot();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        var a = await AddFileAsync(store, "m", "m-00001-of-00002.gguf", TestData.RandomBytes(3000, 7));
        var b = await AddFileAsync(store, "m", "m-00002-of-00002.gguf", TestData.RandomBytes(2000, 8));
        var users = Path.Combine(root.Sub("mine"), "ext.gguf");
        File.WriteAllBytes(users, new byte[5]);
        var ext = await store.AddExternalAsync(users, "chat", "llama-server", Ct);
        var oldRoot = store.Location.Path;
        var foreign = Path.Combine(oldRoot, "users-file.txt");
        File.WriteAllText(foreign, "keep me");

        await store.MoveStoreAsync(root.Sub("new-drive"), null, Ct);
        Assert.Equal(Path.Combine(root.Path, "new-drive", ModelStore.UserFolderName), store.Location.Path);
        var moved = (await store.GetAsync("m", Ct))!;
        Assert.All(moved.Files, f => Assert.StartsWith(store.Location.Path, f.Path, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(TestData.Sha(File.ReadAllBytes(moved.Files[0].Path)), a.Sha256);
        Assert.True(File.Exists(a.Path) && File.Exists(b.Path), "old copies stay until the user removes them");
        Assert.Equal(users, (await store.GetAsync(ext.Id, Ct))!.Files[0].Path);

        var kept = await store.RemoveOldCopiesAsync(Ct);
        Assert.Empty(kept);
        Assert.False(File.Exists(a.Path));
        Assert.False(File.Exists(b.Path));
        Assert.Equal("keep me", File.ReadAllText(foreign));
        Assert.True(File.Exists(users));
        Assert.True(Directory.Exists(oldRoot), "a folder holding a foreign file is never removed");
    }

    [Fact]
    public async Task MoveThatFailsVerificationIsUndoneAndOldStoreStays()
    {
        using var root = new TestRoot();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        var a = await AddFileAsync(store, "m", "a.gguf", TestData.RandomBytes(1000, 9));
        var bytes = File.ReadAllBytes(a.Path);
        bytes[10] ^= 0xFF; // same size, different content: the source no longer matches its recorded hash
        File.WriteAllBytes(a.Path, bytes);
        var oldRoot = store.Location.Path;
        var parent = root.Sub("new-drive");

        await Assert.ThrowsAsync<IOException>(() => store.MoveStoreAsync(parent, null, Ct));
        Assert.Equal(oldRoot, store.Location.Path);
        Assert.True(File.Exists(a.Path));
        Assert.Empty(Directory.GetFileSystemEntries(parent));
    }

    [Fact]
    public async Task OldCopyWithoutVerifiedNewCopyIsKept()
    {
        using var root = new TestRoot();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        var a = await AddFileAsync(store, "m", "a.gguf", TestData.RandomBytes(1000, 10));
        await store.MoveStoreAsync(root.Sub("new-drive"), null, Ct);
        var copy = (await store.GetAsync("m", Ct))!.Files[0].Path;
        File.Delete(copy); // the new copy is gone: the old one must survive

        var kept = await store.RemoveOldCopiesAsync(Ct);
        Assert.Single(kept);
        Assert.True(File.Exists(a.Path));
    }

    [Fact]
    public async Task MoveRefusedWithoutDiskSpace()
    {
        using var root = new TestRoot();
        var disk = new FakeDisk();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), disk);
        await AddFileAsync(store, "m", "a.gguf", new byte[10]);
        disk.FreeBytes = 1L << 30;
        var parent = root.Sub("small");
        await Assert.ThrowsAsync<IOException>(() => store.MoveStoreAsync(parent, null, Ct));
        Assert.Empty(Directory.GetFileSystemEntries(parent));
    }
}
