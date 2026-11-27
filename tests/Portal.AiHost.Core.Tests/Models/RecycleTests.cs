using System.Runtime.CompilerServices;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Store;

namespace Portal.AiHost.Core.Tests.Models;

/// <summary>Stands in for the Recycle Bin: records each path, then removes the test's own temporary file.</summary>
public sealed class FakeRecycler : IFileRecycler
{
    public List<string> Recycled { get; } = [];

    public bool Refuse { get; set; }

    public void AssertCanRecycle(string fullPath, long sizeBytes)
    {
        if (Refuse)
        {
            throw new StoreSafetyException("The Recycle Bin is off for this drive.");
        }
    }

    public void Recycle(string fullPath, long sizeBytes)
    {
        AssertCanRecycle(fullPath, sizeBytes);
        Recycled.Add(fullPath);
        File.Delete(fullPath); // a file inside this test's own temporary store
    }
}

internal static class RecyclerSetup
{
    [ModuleInitializer]
    internal static void UseFakeRecycler() => ModelStore.DefaultRecycler = new FakeRecycler();
}

public class RecycleTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private sealed class FakePolicy(RecycleBinState state) : IRecycleBinPolicy
    {
        public RecycleBinState Query(string fullPath) => state;
    }

    private static async Task<ModelStore> StoreWithModelAsync(TestRoot root, FakeRecycler recycler)
    {
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        store.Recycler = recycler;
        var data = new byte[128];
        var part = await store.BeginFileAsync("tiny", "tiny.gguf", Ct);
        await File.WriteAllBytesAsync(part, data, Ct);
        await store.CommitFileAsync("tiny", part, TestData.Sha(data), Ct);
        return store;
    }

    [Fact]
    public async Task HostOwnedRemovalGoesToTheRecycler()
    {
        using var root = new TestRoot();
        var recycler = new FakeRecycler();
        var store = await StoreWithModelAsync(root, recycler);
        var file = (await store.GetAsync("tiny", Ct))!.Files[0].Path;

        await store.RemoveAsync("tiny", Ct);

        Assert.Equal([file], recycler.Recycled);
        Assert.Null(await store.GetAsync("tiny", Ct));
    }

    [Fact]
    public async Task RefusedBinLeavesTheModelAndItsEntry()
    {
        using var root = new TestRoot();
        var recycler = new FakeRecycler { Refuse = true };
        var store = await StoreWithModelAsync(root, recycler);
        var file = (await store.GetAsync("tiny", Ct))!.Files[0].Path;

        await Assert.ThrowsAsync<StoreSafetyException>(() => store.RemoveAsync("tiny", Ct));

        Assert.True(File.Exists(file));
        Assert.NotNull(await store.GetAsync("tiny", Ct));
        Assert.Empty(recycler.Recycled);
    }

    [Fact]
    public async Task DetectedCopyIsNeverRecycledOnlyForgotten()
    {
        using var root = new TestRoot();
        var users = Path.Combine(root.Sub("OtherApp"), "their-model.gguf");
        File.WriteAllBytes(users, new byte[64]);
        var recycler = new FakeRecycler();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        store.Recycler = recycler;
        var ext = await store.AddExternalAsync(users, "chat", "llama-server", Ct);

        await store.RemoveAsync(ext.Id, Ct);

        Assert.True(File.Exists(users));
        Assert.Equal(64, new FileInfo(users).Length);
        Assert.Empty(recycler.Recycled);
        Assert.Null(await store.GetAsync(ext.Id, Ct));
    }

    [Fact]
    public void PathOutsideTheStoreIsRefused()
    {
        using var root = new TestRoot();
        var outside = Path.Combine(root.Sub("elsewhere"), "x.gguf");
        File.WriteAllBytes(outside, new byte[8]);
        var folder = OwnedFolder.CreateNew(Path.Combine(root.Path, "models"));
        var recycler = new FakeRecycler();

        Assert.ThrowsAny<IOException>(() => folder.RecycleOwnedFile(outside, recycler));
        Assert.ThrowsAny<IOException>(() => folder.RecycleOwnedFile(Path.Combine("..", "elsewhere", "x.gguf"), recycler));
        Assert.True(File.Exists(outside));
        Assert.Empty(recycler.Recycled);
    }

    [Fact]
    public void NukeOnlyVolumeIsRefused()
    {
        var sent = new List<string>();
        var recycler = new ShellRecycler(new FakePolicy(new RecycleBinState(false, null)), sent.Add);

        Assert.Throws<StoreSafetyException>(() => recycler.Recycle(@"C:\models\a.gguf", 10));
        Assert.Empty(sent);
    }

    [Fact]
    public void FileBiggerThanTheBinIsRefused()
    {
        var sent = new List<string>();
        var recycler = new ShellRecycler(new FakePolicy(new RecycleBinState(true, 100)), sent.Add);

        Assert.Throws<StoreSafetyException>(() => recycler.Recycle(@"C:\models\a.gguf", 101));
        Assert.Empty(sent);
    }
}
