using Portal.AiHost.Core.Store;

namespace Portal.AiHost.Core.Tests.Models;

/// <summary>Stores with different markers are strangers: no adopting, no touching each other's files.</summary>
public class StoreOptionsTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly StoreOptions Other = new() { FolderName = "Voice Models", MarkerFileName = ".voice-store", MarkerKind = "voice-store" };

    private static async Task AddAsync(ModelStore s, string id, string name, byte[] data)
    {
        var part = await s.BeginFileAsync(id, name, Ct);
        await File.WriteAllBytesAsync(part, data, Ct);
        await s.CommitFileAsync(id, part, TestData.Sha(data), Ct);
    }

    [Fact]
    public void DefaultsMatchTheHost()
    {
        using var root = new TestRoot();
        var s = ModelStore.OpenOrCreateInParent(root.Sub("p"), new FakeDisk());
        Assert.Equal("Portal AI Host Models", Path.GetFileName(s.Location.Path));
        Assert.True(File.Exists(Path.Combine(s.Location.Path, ".portal-ai-host-store")));
    }

    [Fact]
    public void CustomOptionsNameFolderAndMarker()
    {
        using var root = new TestRoot();
        var s = ModelStore.OpenOrCreateInParent(root.Sub("p"), new FakeDisk(), Other);
        Assert.Equal("Voice Models", Path.GetFileName(s.Location.Path));
        Assert.True(File.Exists(Path.Combine(s.Location.Path, ".voice-store")));
        Assert.False(File.Exists(Path.Combine(s.Location.Path, ".portal-ai-host-store")));
    }

    [Fact]
    public async Task StoresNeverAdoptEachOthersFolders()
    {
        using var root = new TestRoot();
        var host = ModelStore.OpenOrCreate(Path.Combine(root.Path, "host"), new FakeDisk());
        await host.EnsureModelAsync("m", "M", "chat", "llama", Ct);
        var voice = ModelStore.OpenOrCreate(Path.Combine(root.Path, "voice"), new FakeDisk(), Other);

        Assert.Throws<StoreRefusedException>(() => ModelStore.Open(host.Location.Path, new FakeDisk(), Other));
        Assert.Throws<StoreRefusedException>(() => ModelStore.OpenOrCreate(host.Location.Path, new FakeDisk(), Other));
        Assert.Throws<StoreRefusedException>(() => ModelStore.Open(voice.Location.Path, new FakeDisk()));
        Assert.Throws<StoreRefusedException>(() => ModelStore.OpenOrCreate(voice.Location.Path, new FakeDisk()));
        // A same-named marker file with the other kind inside is not adopted either.
        var fake = root.Sub("fake");
        File.WriteAllText(Path.Combine(fake, ".voice-store"), "{\"kind\":\"portal-ai-host-store\",\"storeId\":\"" + Guid.NewGuid() + "\"}");
        Assert.Throws<StoreRefusedException>(() => ModelStore.Open(fake, new FakeDisk(), Other));
    }

    [Fact]
    public async Task DeletingInOneStoreNeverTouchesTheOtherOrForeignFiles()
    {
        using var root = new TestRoot();
        var host = ModelStore.OpenOrCreate(Path.Combine(root.Path, "host"), new FakeDisk());
        var voice = ModelStore.OpenOrCreate(Path.Combine(root.Path, "voice"), new FakeDisk(), Other);
        byte[] a = [1, 2, 3], b = [4, 5, 6, 7];
        await host.EnsureModelAsync("shared-id", "A", "chat", "llama", Ct);
        await voice.EnsureModelAsync("shared-id", "B", "stt", "whisper", Ct);
        await AddAsync(host, "shared-id", "x.gguf", a);
        await AddAsync(voice, "shared-id", "x.gguf", b);
        var foreign = Path.Combine(root.Path, "host", "shared-id", "foreign.txt");
        File.WriteAllText(foreign, "mine");
        var outside = Path.Combine(root.Sub("elsewhere"), "keep.bin");
        File.WriteAllBytes(outside, [9]);

        await voice.RemoveAsync("shared-id", Ct);

        Assert.Equal(a, File.ReadAllBytes(Path.Combine(root.Path, "host", "shared-id", "x.gguf")));
        Assert.Equal("mine", File.ReadAllText(foreign));
        Assert.Equal([9], File.ReadAllBytes(outside));
        Assert.NotNull(await host.GetAsync("shared-id", Ct));
        Assert.Null(await voice.GetAsync("shared-id", Ct));

        await host.RemoveAsync("shared-id", Ct);
        Assert.Equal("mine", File.ReadAllText(foreign));
    }

    [Fact]
    public void BadNamesAreRefused()
    {
        using var root = new TestRoot();
        Assert.Throws<ArgumentException>(() => ModelStore.OpenOrCreate(root.Sub("a"), new FakeDisk(), new StoreOptions { MarkerFileName = "store.json" }));
        Assert.Throws<ArgumentException>(() => ModelStore.OpenOrCreate(root.Sub("b"), new FakeDisk(), new StoreOptions { MarkerFileName = "a/b" }));
    }

    [Fact]
    public async Task AdoptFromPathCopiesAndLeavesTheSourceUntouched()
    {
        using var root = new TestRoot();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        var src = Path.Combine(root.Sub("Downloads"), "w.bin");
        byte[] data = [1, 2, 3, 4, 5];
        File.WriteAllBytes(src, data);
        var stamp = File.GetLastWriteTimeUtc(src);

        var stored = await store.AdoptFromPathAsync("whisper", "w.bin", src, TestData.Sha(data), data.Length, Ct);

        Assert.NotNull(stored);
        Assert.True(File.Exists(src));
        Assert.Equal(data, File.ReadAllBytes(src));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(src));
        var copy = Path.Combine(store.Location.Path, "whisper", "w.bin");
        Assert.Equal(data, File.ReadAllBytes(copy));
    }

    [Fact]
    public async Task AdoptFromPathWithWrongHashKeepsSourceAndLeavesNoCopy()
    {
        using var root = new TestRoot();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        var src = Path.Combine(root.Sub("Downloads"), "w.bin");
        byte[] data = [1, 2, 3, 4, 5];
        File.WriteAllBytes(src, data);

        Assert.Null(await store.AdoptFromPathAsync("whisper", "w.bin", src, TestData.Sha([9, 9, 9, 9, 9]), data.Length, Ct));
        Assert.Null(await store.AdoptFromPathAsync("whisper", "w.bin", src, TestData.Sha(data), data.Length + 1, Ct));

        Assert.Equal(data, File.ReadAllBytes(src));
        Assert.Null(await store.GetAsync("whisper", Ct));
        Assert.Empty(Directory.EnumerateFiles(store.Location.Path, "*.part*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task AdoptFromPathRefusesAFileInsideTheStore()
    {
        using var root = new TestRoot();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        await Assert.ThrowsAsync<StoreRefusedException>(() =>
            store.AdoptFromPathAsync("m", "f.bin", Path.Combine(store.Location.Path, "store.json"), "00", 1, Ct));
    }
}
