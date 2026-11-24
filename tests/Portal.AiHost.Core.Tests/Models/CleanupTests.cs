using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Store;

namespace Portal.AiHost.Core.Tests.Models;

/// <summary>"Free up space": only host-created leftovers are candidates, and only unchanged ones are removed.</summary>
public class CleanupTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly TimeSpan Week = TimeSpan.FromDays(7);

    private static async Task<StoredFile> AddFileAsync(ModelStore store, string modelId, string name, byte[] data)
    {
        var part = await store.BeginFileAsync(modelId, name, Ct);
        await File.WriteAllBytesAsync(part, data, Ct);
        return await store.CommitFileAsync(modelId, part, TestData.Sha(data), Ct);
    }

    /// <summary>An unfinished download with a resume sidecar, last touched <paramref name="ageDays"/> days ago.</summary>
    private static async Task<string> AddPartialAsync(ModelStore store, string modelId, string name, int bytes, double ageDays)
    {
        await store.EnsureModelAsync(modelId, modelId, "chat", "llama-server", Ct);
        var part = await store.BeginFileAsync(modelId, name, Ct);
        await File.WriteAllBytesAsync(part, TestData.RandomBytes(bytes, 3), Ct);
        await store.WriteResumeStateAsync(modelId, part, "{}", Ct);
        var when = DateTime.UtcNow.AddDays(-ageDays);
        File.SetLastWriteTimeUtc(part, when);
        File.SetLastWriteTimeUtc(part + ".json", when);
        return part;
    }

    private static ModelStore NewStore(TestRoot root) => ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());

    private static async Task<IReadOnlyList<CleanupCandidate>> ScanAsync(ModelStore s) => await s.ScanLeftoversAsync(Week, Ct);

    [Fact]
    public async Task OldPartialDownloadIsACandidateAndRemovalDeletesPartSidecarAndEmptyRecord()
    {
        using var root = new TestRoot();
        var store = NewStore(root);
        var part = await AddPartialAsync(store, "m", "m.gguf", 4000, ageDays: 9);

        var found = Assert.Single(await ScanAsync(store));
        Assert.Equal(CleanupKind.PartialDownload, found.Kind);
        Assert.True(found.Bytes >= 4000);

        var result = await store.CleanLeftoversAsync([found.Id], Week, Ct);

        Assert.Equal(found.Bytes, result.FreedBytes);
        Assert.Empty(result.Skipped);
        Assert.False(File.Exists(part));
        Assert.False(File.Exists(part + ".json"));
        Assert.Null(await store.GetAsync("m", Ct));
        Assert.False(Directory.Exists(Path.GetDirectoryName(part)));
    }

    [Fact]
    public async Task PartialDownloadAgeRuleKeepsResumableOnes()
    {
        using var root = new TestRoot();
        var store = NewStore(root);
        var fresh = await AddPartialAsync(store, "fresh", "f.gguf", 100, ageDays: 6.9);
        var old = await AddPartialAsync(store, "old", "o.gguf", 100, ageDays: 7.1);

        var found = Assert.Single(await ScanAsync(store));
        Assert.Equal("old", found.Label);
        await store.CleanLeftoversAsync([found.Id], Week, Ct);
        Assert.True(File.Exists(fresh));
        Assert.False(File.Exists(old));

        // a sidecar touched recently keeps an old .part
        var resumed = await AddPartialAsync(store, "resumed", "r.gguf", 100, ageDays: 20);
        File.SetLastWriteTimeUtc(resumed + ".json", DateTime.UtcNow);
        Assert.Empty(await ScanAsync(store));
        Assert.True(File.Exists(resumed));
    }

    [Fact]
    public async Task ActiveDownloadIsNeverACandidate()
    {
        using var root = new TestRoot();
        var store = NewStore(root);
        var part = await AddPartialAsync(store, "m", "m.gguf", 100, ageDays: 30);
        using (store.AcquireDownload("m"))
        {
            Assert.Empty(await ScanAsync(store));
        }

        Assert.True(File.Exists(part));
        Assert.Single(await ScanAsync(store));
    }

    [Fact]
    public async Task PartChangedBetweenScanAndCleanIsSkipped()
    {
        using var root = new TestRoot();
        var store = NewStore(root);
        var part = await AddPartialAsync(store, "m", "m.gguf", 1000, ageDays: 10);
        var found = Assert.Single(await ScanAsync(store));

        await File.AppendAllTextAsync(part, "more bytes arrived"); // touched and longer: a resume wrote to it
        var result = await store.CleanLeftoversAsync([found.Id], Week, Ct);

        Assert.Equal(0, result.FreedBytes);
        Assert.Single(result.Skipped);
        Assert.True(File.Exists(part));
    }

    [Fact]
    public async Task PartReplacedByAnotherFileIsNotACandidateAndNotDeleted()
    {
        using var root = new TestRoot();
        var store = NewStore(root);
        var part = await AddPartialAsync(store, "m", "m.gguf", 1000, ageDays: 10);
        var found = Assert.Single(await ScanAsync(store));

        File.Delete(part); // another file now sits at that name: a different file ID
        await File.WriteAllBytesAsync(part, new byte[1000], Ct);
        File.SetLastWriteTimeUtc(part, DateTime.UtcNow.AddDays(-10));

        Assert.Empty(await ScanAsync(store));
        var result = await store.CleanLeftoversAsync([found.Id], Week, Ct);
        Assert.Single(result.Skipped);
        Assert.True(File.Exists(part));
    }

    [Fact]
    public async Task HardLinkedPartIsNotACandidate()
    {
        using var root = new TestRoot();
        var store = NewStore(root);
        var part = await AddPartialAsync(store, "m", "m.gguf", 1000, ageDays: 10);
        var outside = Path.Combine(root.Sub("elsewhere"), "link.bin");
        TestRoot.CreateHardLink(outside, part);

        Assert.Empty(await ScanAsync(store));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task JunctionedModelFolderIsNeverFollowed()
    {
        using var root = new TestRoot();
        var store = NewStore(root);
        var part = await AddPartialAsync(store, "m", "m.gguf", 1000, ageDays: 10);
        var folder = Path.GetDirectoryName(part)!;
        var real = root.Sub("realplace");
        foreach (var f in Directory.GetFiles(folder))
        {
            File.Move(f, Path.Combine(real, Path.GetFileName(f)));
        }

        Directory.Delete(folder);
        TestRoot.CreateJunction(folder, real);

        Assert.Empty(await ScanAsync(store));
        Assert.True(File.Exists(Path.Combine(real, Path.GetFileName(part))));
    }

    [Fact]
    public async Task HealthyModelsAndUnrecordedFilesAreNeverCandidates()
    {
        using var root = new TestRoot();
        var store = NewStore(root);
        var healthy = await AddFileAsync(store, "good", "good.gguf", TestData.RandomBytes(2000, 5));
        File.SetLastWriteTimeUtc(healthy.Path, DateTime.UtcNow.AddDays(-400));
        var part = await AddPartialAsync(store, "m", "m.gguf", 100, ageDays: 30);
        var stray = Path.Combine(Path.GetDirectoryName(part)!, "stray.gguf.part"); // looks like a leftover, but the host never recorded it
        File.WriteAllText(stray, "not ours");
        File.SetLastWriteTimeUtc(stray, DateTime.UtcNow.AddDays(-400));

        var found = Assert.Single(await ScanAsync(store));
        var result = await store.CleanLeftoversAsync([found.Id, "part|good|good.gguf|x|1|0|1", "forged"], Week, Ct);

        Assert.True(File.Exists(healthy.Path));
        Assert.True(File.Exists(stray));
        Assert.False(File.Exists(part));
        Assert.Equal(2, result.Skipped.Count);
        Assert.NotNull(await store.GetAsync("good", Ct));
    }

    [Fact]
    public async Task AdoptedOriginalAndOtherAppsStoreAreUntouched()
    {
        using var root = new TestRoot();
        var store = NewStore(root);
        var original = Path.Combine(root.Sub("Downloads"), "w.bin");
        byte[] data = [1, 2, 3, 4, 5];
        File.WriteAllBytes(original, data);
        File.SetLastWriteTimeUtc(original, DateTime.UtcNow.AddDays(-300));
        await store.AdoptFromPathAsync("whisper", "w.bin", original, TestData.Sha(data), data.Length, Ct);

        var other = ModelStore.OpenOrCreate(Path.Combine(root.Path, "other"), new FakeDisk(), StoreOptions.ForApp("voice-portal"));
        var otherPart = await AddPartialAsync(other, "x", "x.gguf", 100, ageDays: 50);
        var otherFound = Assert.Single(await ScanAsync(other));

        Assert.Empty(await ScanAsync(store));
        var result = await store.CleanLeftoversAsync([otherFound.Id], Week, Ct);

        Assert.Equal(data, File.ReadAllBytes(original));
        Assert.True(File.Exists(otherPart));
        Assert.Single(result.Skipped);
        Assert.NotNull(await store.GetAsync("whisper", Ct));
    }

    // ---- old copies after a move ----

    private static async Task<(ModelStore Store, string OldFile, string OldRoot)> MovedAsync(TestRoot root)
    {
        var store = NewStore(root);
        var a = await AddFileAsync(store, "m", "a.gguf", TestData.RandomBytes(1500, 10));
        var oldRoot = store.Location.Path;
        await store.MoveStoreAsync(root.Sub("new-drive"), null, Ct);
        return (store, a.Path, oldRoot);
    }

    [Fact]
    public async Task OldCopiesWithVerifiedNewCopyAreRemovedAndTheNewCopyStays()
    {
        using var root = new TestRoot();
        var (store, oldFile, oldRoot) = await MovedAsync(root);
        var foreign = Path.Combine(oldRoot, "users-file.txt");
        File.WriteAllText(foreign, "keep me");
        var newCopy = (await store.GetAsync("m", Ct))!.Files[0].Path;

        var found = Assert.Single(await ScanAsync(store));
        Assert.Equal(CleanupKind.OldCopies, found.Kind);
        Assert.Equal(1500, found.Bytes);

        var result = await store.CleanLeftoversAsync([found.Id], Week, Ct);

        Assert.Equal(1500, result.FreedBytes);
        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(newCopy));
        Assert.Equal("keep me", File.ReadAllText(foreign));
        Assert.Empty(await ScanAsync(store));
    }

    [Fact]
    public async Task OldCopyWithoutVerifiedNewCopyIsNotACandidate()
    {
        using var root = new TestRoot();
        var (store, oldFile, _) = await MovedAsync(root);
        File.Delete((await store.GetAsync("m", Ct))!.Files[0].Path);

        Assert.Empty(await ScanAsync(store));
        Assert.True(File.Exists(oldFile));
    }

    [Fact]
    public async Task OldCopiesAreSkippedWhileAnotherProgramHoldsTheOldStoreLock()
    {
        using var root = new TestRoot();
        var (store, oldFile, oldRoot) = await MovedAsync(root);
        var found = Assert.Single(await ScanAsync(store));

        using (ModelStore.Open(oldRoot)) // the old folder's .store-lock is held
        {
            var result = await store.CleanLeftoversAsync([found.Id], Week, Ct);
            Assert.Equal(0, result.FreedBytes);
            Assert.Single(result.Skipped);
            Assert.True(File.Exists(oldFile));
        }
    }

    [Fact]
    public async Task OldCopyChangedSinceTheScanIsKept()
    {
        using var root = new TestRoot();
        var (store, oldFile, _) = await MovedAsync(root);
        var found = Assert.Single(await ScanAsync(store));
        await File.AppendAllTextAsync(oldFile, "x"); // size no longer matches what the old store.json recorded

        var result = await store.CleanLeftoversAsync([found.Id], Week, Ct);

        Assert.Equal(0, result.FreedBytes);
        Assert.True(File.Exists(oldFile));
    }
}
