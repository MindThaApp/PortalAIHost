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
        File.WriteAllText(Path.Combine(fake, ".voice-store"), "{\"kind\":\"portal-ai-host-store\",\"store_id\":\"" + Guid.NewGuid() + "\"}");
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

        var result = await store.AdoptFromPathAsync("whisper", "w.bin", src, TestData.Sha(data), data.Length, Ct);

        Assert.Equal(AdoptStatus.Adopted, result.Status);
        Assert.NotNull(result.File);
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

        Assert.Equal(AdoptStatus.HashMismatch, (await store.AdoptFromPathAsync("whisper", "w.bin", src, TestData.Sha([9, 9, 9, 9, 9]), data.Length, Ct)).Status);
        Assert.Equal(AdoptStatus.SizeMismatch, (await store.AdoptFromPathAsync("whisper", "w.bin", src, TestData.Sha(data), data.Length + 1, Ct)).Status);

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

    // ---- review fixes -------------------------------------------------------------

    private static string Src(TestRoot root, byte[] data, string name = "w.bin")
    {
        var src = Path.Combine(root.Sub("Downloads"), name);
        File.WriteAllBytes(src, data);
        return src;
    }

    [Fact]
    public void ChangingOnlyTheFolderNameIsRefused()
    {
        using var root = new TestRoot();
        Assert.Throws<ArgumentException>(() => ModelStore.OpenOrCreate(root.Sub("a"), new FakeDisk(), new StoreOptions { FolderName = "Other Models" }));
        Assert.Throws<ArgumentException>(() => ModelStore.OpenOrCreate(root.Sub("b"), new FakeDisk(), new StoreOptions { FolderName = "Other Models", MarkerKind = "x-store" }));
        Assert.Throws<ArgumentException>(() => ModelStore.OpenOrCreate(root.Sub("c"), new FakeDisk(), new StoreOptions { FolderName = "Other Models", MarkerFileName = ".x-store" }));
    }

    [Fact]
    public void ForAppDerivesAllThreeNamesAndNeverOpensTheHostsFolder()
    {
        using var root = new TestRoot();
        var app = StoreOptions.ForApp("voice-portal");
        Assert.Equal("voice-portal Models", app.FolderName);
        Assert.Equal(".voice-portal-store", app.MarkerFileName);
        Assert.Equal("voice-portal-store", app.MarkerKind);

        var host = ModelStore.OpenOrCreate(Path.Combine(root.Path, "host"), new FakeDisk());
        var mine = ModelStore.OpenOrCreate(Path.Combine(root.Path, "mine"), new FakeDisk(), app);
        Assert.Throws<StoreRefusedException>(() => ModelStore.Open(host.Location.Path, new FakeDisk(), app));
        Assert.Throws<StoreRefusedException>(() => ModelStore.Open(mine.Location.Path, new FakeDisk()));
        Assert.Throws<ArgumentException>(() => StoreOptions.ForApp("portal-ai-host"));
        Assert.Throws<ArgumentException>(() => StoreOptions.ForApp("Bad Key"));
        Assert.Throws<ArgumentException>(() => StoreOptions.ForApp(""));
    }

    [Theory]
    [InlineData("store.json")]
    [InlineData("STORE.JSON.TMP")]
    [InlineData(".a.part")]
    [InlineData(".a.json")]
    [InlineData(".a.tmp")]
    [InlineData(".A-store")]
    [InlineData("a-store")]
    [InlineData(".")]
    [InlineData(".-x")]
    public void BadMarkerNamesAreRefused(string name)
    {
        using var root = new TestRoot();
        Assert.Throws<ArgumentException>(() => ModelStore.OpenOrCreate(root.Sub("m"), new FakeDisk(),
            new StoreOptions { FolderName = "Okay Models", MarkerFileName = name, MarkerKind = "okay-store" }));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("lpt1")]
    [InlineData("name.")]
    [InlineData("name ")]
    [InlineData("..")]
    [InlineData("a:b")]
    [InlineData("-lead")]
    [InlineData("store.json")]
    [InlineData("x.part")]
    [InlineData("x.TMP")]
    public void BadFolderNamesAreRefused(string name)
    {
        using var root = new TestRoot();
        Assert.Throws<ArgumentException>(() => ModelStore.OpenOrCreate(root.Sub("f"), new FakeDisk(),
            new StoreOptions { FolderName = name, MarkerFileName = ".okay-store", MarkerKind = "okay-store" }));
    }

    [Fact]
    public void AStoreCannotBeOpenedTwiceAtOnce()
    {
        using var root = new TestRoot();
        var path = Path.Combine(root.Path, "models");
        var first = ModelStore.OpenOrCreate(path, new FakeDisk());
        var ex = Assert.Throws<StoreLockedException>(() => ModelStore.Open(path, new FakeDisk()));
        Assert.Contains("already open", ex.Message, StringComparison.Ordinal);
        first.Dispose();
        ModelStore.Open(path, new FakeDisk()).Dispose();
        Assert.True(File.Exists(Path.Combine(path, ".portal-ai-host-store")));
        Assert.False(File.Exists(Path.Combine(path, ".store-lock")));
    }

    [Fact]
    public async Task MovingTheStoreHandsTheLockOver()
    {
        using var root = new TestRoot();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        var oldPath = store.Location.Path;
        await AddAsync(store, "m", "a.gguf", [1, 2, 3]);
        await store.MoveStoreAsync(root.Sub("new"), null, Ct);

        Assert.Throws<StoreLockedException>(() => ModelStore.Open(store.Location.Path, new FakeDisk()));
        ModelStore.Open(oldPath, new FakeDisk()).Dispose(); // old folder is free again
    }

    [Fact]
    public async Task AdoptRefusesRelativeSourceAndSymlinkSource()
    {
        using var root = new TestRoot();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        byte[] data = [1, 2, 3];
        await Assert.ThrowsAsync<ArgumentException>(() => store.AdoptFromPathAsync("m", "w.bin", "w.bin", TestData.Sha(data), 3, Ct));

        var real = Src(root, data);
        var link = Path.Combine(root.Sub("Links"), "l.bin");
        try
        {
            File.CreateSymbolicLink(link, real);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return; // no symlink right on this machine
        }

        Assert.Equal(AdoptStatus.SourceIsLink, (await store.AdoptFromPathAsync("m", "w.bin", link, TestData.Sha(data), 3, Ct)).Status);
        Assert.Equal(data, File.ReadAllBytes(real));
    }

    [Fact]
    public async Task AdoptRefusesAHardLinkIntoTheStore()
    {
        using var root = new TestRoot();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        byte[] data = [1, 2, 3, 4];
        await AddAsync(store, "m", "a.bin", data);
        var link = Path.Combine(root.Sub("Elsewhere"), "link.bin");
        TestRoot.CreateHardLink(link, Path.Combine(store.Location.Path, "m", "a.bin"));
        await Assert.ThrowsAsync<StoreRefusedException>(() => store.AdoptFromPathAsync("n", "n.bin", link, TestData.Sha(data), 4, Ct));
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(store.Location.Path, "m", "a.bin")));
    }

    [Fact]
    public async Task AdoptNeverTouchesAPausedDownloadsPart()
    {
        using var root = new TestRoot();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        byte[] data = [1, 2, 3, 4, 5];
        var part = await store.BeginFileAsync("m", "w.bin", Ct);
        await File.WriteAllBytesAsync(part, [7, 7], Ct);

        var result = await store.AdoptFromPathAsync("m", "w.bin", Src(root, data), TestData.Sha(data), 5, Ct);

        Assert.Equal(AdoptStatus.PartialDownloadExists, result.Status);
        Assert.Equal<byte>([7, 7], File.ReadAllBytes(part));
        Assert.NotNull(await store.GetAsync("m", Ct));
    }

    [Fact]
    public async Task AdoptIsRefusedWhileTheModelIsBusy()
    {
        using var root = new TestRoot();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        byte[] data = [1, 2, 3];
        using (store.AcquireDownload("m"))
        {
            Assert.Equal(AdoptStatus.Busy, (await store.AdoptFromPathAsync("m", "w.bin", Src(root, data), TestData.Sha(data), 3, Ct)).Status);
        }

        Assert.Equal(AdoptStatus.Adopted, (await store.AdoptFromPathAsync("m", "w.bin", Src(root, data), TestData.Sha(data), 3, Ct)).Status);
        Assert.Equal(AdoptStatus.AlreadyStored, (await store.AdoptFromPathAsync("m", "w.bin", Src(root, data), TestData.Sha(data), 3, Ct)).Status);
        var other = Src(root, [9, 9, 9], "o.bin");
        Assert.Equal(AdoptStatus.DifferentFileStored, (await store.AdoptFromPathAsync("m", "w.bin", other, TestData.Sha([9, 9, 9]), 3, Ct)).Status);
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(store.Location.Path, "m", "w.bin")));
    }

    [Fact]
    public async Task AdoptChecksFreeSpaceFirstAndKeepsAnExistingModelRecord()
    {
        using var root = new TestRoot();
        var disk = new FakeDisk(freeBytes: 1000);
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), disk);
        byte[] data = [1, 2, 3];
        Assert.Equal(AdoptStatus.NotEnoughSpace, (await store.AdoptFromPathAsync("m", "w.bin", Src(root, data), TestData.Sha(data), 3, Ct)).Status);
        Assert.Null(await store.GetAsync("m", Ct));

        disk.FreeBytes = long.MaxValue / 4;
        await store.EnsureModelAsync("keep", "Keep", "chat", "llama", Ct);
        Assert.Equal(AdoptStatus.HashMismatch, (await store.AdoptFromPathAsync("keep", "w.bin", Src(root, data), TestData.Sha([0]), 3, Ct)).Status);
        Assert.NotNull(await store.GetAsync("keep", Ct)); // the record existed before, so it stays
        Assert.Null(await store.GetAsync("m", Ct));
    }

    [Fact]
    public async Task CommitWithTheWrongExpectedSizeIsRefusedAndKeepsThePart()
    {
        using var root = new TestRoot();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        var part = await store.BeginFileAsync("m", "w.bin", Ct);
        await File.WriteAllBytesAsync(part, [1, 2, 3], Ct);
        await Assert.ThrowsAsync<StoreSafetyException>(() => store.CommitFileAsync("m", part, TestData.Sha([1, 2, 3]), 5, Ct));
        Assert.True(File.Exists(part));
    }

    [Fact]
    public async Task UseAfterDisposeThrowsObjectDisposed()
    {
        using var root = new TestRoot();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        store.Dispose();
        store.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.ListAsync(Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.EnsureModelAsync("m", "M", "chat", "llama", Ct));
    }

    [Fact]
    public async Task RemovingOldCopiesIsRefusedWhileTheOldStoreIsOpenAndNeverLeavesGhosts()
    {
        using var root = new TestRoot();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        var oldPath = store.Location.Path;
        await AddAsync(store, "m", "a.gguf", [1, 2, 3]);
        await store.MoveStoreAsync(root.Sub("new"), null, Ct);

        using (ModelStore.Open(oldPath, new FakeDisk()))
        {
            await Assert.ThrowsAsync<StoreLockedException>(() => store.RemoveOldCopiesAsync(Ct));
            Assert.True(File.Exists(Path.Combine(oldPath, "m", "a.gguf")));
        }

        File.WriteAllText(Path.Combine(oldPath, "user-note.txt"), "mine"); // keeps the old folder from being retired
        Assert.Empty(await store.RemoveOldCopiesAsync(Ct));

        Assert.Equal("mine", File.ReadAllText(Path.Combine(oldPath, "user-note.txt")));
        using var old = ModelStore.Open(oldPath, new FakeDisk());
        Assert.Empty(await old.ListAsync(Ct)); // no listed model whose files are gone
    }
}
