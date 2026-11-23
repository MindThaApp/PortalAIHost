using System.Net;
using System.Text;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Catalogue;
using Portal.AiHost.Core.Download;
using Portal.AiHost.Core.Planning;
using Portal.AiHost.Core.Store;

namespace Portal.AiHost.Core.Tests.Models;

/// <summary>Regression tests for the review findings on the store, downloader, catalogue and planner.</summary>
public class SafetyFixTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const long GiB = 1L << 30;

    private static DownloadOptions Fast(int? maxAttempts = 3) => new()
    {
        RetryDelays = [TimeSpan.Zero],
        RetryEvery = TimeSpan.Zero,
        MaxAttempts = maxAttempts,
        BufferBytes = 64 * 1024,
        DiskCheckIntervalBytes = 128 * 1024,
        FixedHeadroomBytes = 0,
    };

    private static void Serve(RangeServer server, CatalogueEntry entry, params byte[][] data)
    {
        for (var i = 0; i < entry.Files.Count; i++)
        {
            server.Files[new Uri(entry.Files[i].Urls[0]).AbsolutePath] = data[i];
        }
    }

    private sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }

    // ---- 2: split parts are never renamed; crash leftovers are adopted or reported ----

    [Fact]
    public async Task PartRenamedJustBeforeCrashIsAdoptedNotDownloadedAgain()
    {
        using var root = new TestRoot();
        await using var server = new RangeServer();
        var parts = new[] { TestData.RandomBytes(5000, 1), TestData.RandomBytes(6000, 2) };
        var entry = TestData.Entry("big", ("big-00001-of-00002.gguf", parts[0]), ("big-00002-of-00002.gguf", parts[1]));
        Serve(server, entry, parts);
        var folder = Path.Combine(root.Path, "models");
        var store = ModelStore.OpenOrCreate(folder, new FakeDisk());
        await store.EnsureModelAsync("big", "big", "chat", "llama-server", Ct);
        foreach (var (name, data) in new[] { ("big-00001-of-00002.gguf", parts[0]), ("big-00002-of-00002.gguf", parts[1]) })
        {
            var part = await store.BeginFileAsync("big", name, Ct);
            await File.WriteAllBytesAsync(part, data, Ct);
            File.Move(part, part[..^".part".Length]); // crash: renamed, store.json still says partial
        }

        store.Dispose();
        var reopened = ModelStore.Open(folder, new FakeDisk());
        using var http = server.CreateClient();
        var model = await new ModelDownloader(http, reopened, new FakeDisk(), Fast()).DownloadAsync(entry, null, Ct);

        Assert.Empty(server.Requests);
        Assert.Equal(["big-00001-of-00002.gguf", "big-00002-of-00002.gguf"], model.Files.Select(f => Path.GetFileName(f.Path)).Order());
        Assert.All(model.Files, f => Assert.Equal(StoredFileState.Verified, f.State));
        Assert.DoesNotContain(Directory.GetFiles(folder, "*", SearchOption.AllDirectories), f => f.Contains('(', StringComparison.Ordinal));
    }

    [Fact]
    public async Task ForeignFileAtFinalNameStopsTheDownload()
    {
        using var root = new TestRoot();
        await using var server = new RangeServer();
        var data = TestData.RandomBytes(5000, 3);
        var entry = TestData.Entry("m", ("m.gguf", data));
        Serve(server, entry, data);
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        await store.EnsureModelAsync("m", "m", "chat", "llama-server", Ct);
        var foreign = Path.Combine(store.Location.Path, "m", "m.gguf");
        File.WriteAllText(foreign, "user's own file");
        using var http = server.CreateClient();

        await Assert.ThrowsAsync<StoreSafetyException>(() =>
            new ModelDownloader(http, store, new FakeDisk(), Fast(maxAttempts: null)).DownloadAsync(entry, null, Ct));
        Assert.Equal("user's own file", File.ReadAllText(foreign));
        Assert.Empty(server.Requests);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(foreign)!));
    }

    [Fact]
    public async Task OrphanPartIsLeftAloneAndReported()
    {
        using var root = new TestRoot();
        await using var server = new RangeServer();
        var data = TestData.RandomBytes(5000, 4);
        var entry = TestData.Entry("m", ("m.gguf", data));
        Serve(server, entry, data);
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        await store.EnsureModelAsync("m", "m", "chat", "llama-server", Ct);
        var orphan = Path.Combine(store.Location.Path, "m", "m.gguf.part");
        File.WriteAllText(orphan, "left by a crash, not recorded");
        using var http = server.CreateClient();

        var model = await new ModelDownloader(http, store, new FakeDisk(), Fast()).DownloadAsync(entry, null, Ct);
        Assert.Equal("m.gguf", Path.GetFileName(model.Files[0].Path));
        Assert.Equal("left by a crash, not recorded", File.ReadAllText(orphan));
        Assert.Contains(orphan, await store.ListUnrecordedFilesAsync(Ct), StringComparer.OrdinalIgnoreCase);

        await store.RemoveAsync("m", Ct);
        Assert.True(File.Exists(orphan));
    }

    // ---- 3: a hash-mismatch retry keeps the model record ----

    [Fact]
    public async Task HashRetryKeepsKindAndEngine()
    {
        using var root = new TestRoot();
        await using var server = new RangeServer();
        var data = TestData.RandomBytes(50_000, 5);
        var bad = (byte[])data.Clone();
        bad[100] ^= 1;
        var entry = TestData.Entry("m", ("m.gguf", data));
        Serve(server, entry, data);
        server.Override = n => n == 1 ? bad : null;
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        using var http = server.CreateClient();

        await new ModelDownloader(http, store, new FakeDisk(), Fast()).DownloadAsync(entry, null, Ct);
        var model = (await store.GetAsync("m", Ct))!;
        Assert.Equal(2, server.Requests.Count);
        Assert.Equal("chat", model.Kind);
        Assert.Equal("llama-server", model.Engine);
        Assert.Equal("m", model.DisplayName);
    }

    // ---- 4: fatal errors are not retried; one download per model ----

    [Fact]
    public void RetryClassification()
    {
        Assert.False(ModelDownloader.IsRetryable(new StoreSafetyException("x")));
        Assert.False(ModelDownloader.IsRetryable(new StoreRefusedException("x")));
        Assert.False(ModelDownloader.IsRetryable(new IOException("full", unchecked((int)0x80070070))));
        Assert.False(ModelDownloader.IsRetryable(new IOException("full", unchecked((int)0x80070027))));
        Assert.False(ModelDownloader.IsRetryable(new IOException("in use", unchecked((int)0x80070020))));
        Assert.True(ModelDownloader.IsDiskFull(new IOException("full", unchecked((int)0x80070070))));
        Assert.True(ModelDownloader.IsRetryable(new IOException("connection reset")));
        Assert.True(ModelDownloader.IsRetryable(new HttpRequestException("busy", null, HttpStatusCode.ServiceUnavailable)));
        Assert.False(ModelDownloader.IsRetryable(new HttpRequestException("gone", null, HttpStatusCode.NotFound)));
    }

    [Fact]
    public async Task StoreSafetyRefusalDuringDownloadIsFatal()
    {
        using var root = new TestRoot();
        await using var server = new RangeServer { ChunkDelay = TimeSpan.FromMilliseconds(5) };
        var data = TestData.RandomBytes(1_000_000, 6);
        var entry = TestData.Entry("m", ("m.gguf", data));
        Serve(server, entry, data);
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        var marker = Path.Combine(store.Location.Path, ".portal-ai-host-store");
        using var http = server.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var swapped = 0;
        var progress = new SyncProgress<DownloadProgress>(p =>
        {
            if (p.BytesDone > 50_000 && Interlocked.Exchange(ref swapped, 1) == 0)
            {
                File.WriteAllText(marker, File.ReadAllText(marker).Replace(store.Location.StoreId.ToString(), Guid.NewGuid().ToString()));
            }
        });

        await Assert.ThrowsAsync<StoreSafetyException>(() =>
            new ModelDownloader(http, store, new FakeDisk(), Fast(maxAttempts: null)).DownloadAsync(entry, progress, timeout.Token));
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task FullDiskReportsPaused()
    {
        using var root = new TestRoot();
        await using var server = new RangeServer { ChunkDelay = TimeSpan.FromMilliseconds(30) };
        var data = TestData.RandomBytes(1_000_000, 7);
        var entry = TestData.Entry("m", ("m.gguf", data));
        Serve(server, entry, data);
        var disk = new FakeDisk(freeBytes: 10_000_000);
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), disk);
        using var http = server.CreateClient();
        var states = new List<string>();
        var progress = new SyncProgress<DownloadProgress>(p =>
        {
            lock (states)
            {
                states.Add(p.State);
            }

            if (p.BytesDone > 200_000)
            {
                disk.FreeBytes = 10;
            }
        });

        await Assert.ThrowsAsync<InsufficientDiskSpaceException>(() =>
            new ModelDownloader(http, store, disk, Fast()).DownloadAsync(entry, progress, Ct));
        Assert.Equal("paused", states[^1]);
    }

    [Fact]
    public async Task SecondDownloadOfSameModelJoinsOrIsRefused()
    {
        using var root = new TestRoot();
        await using var server = new RangeServer { ChunkDelay = TimeSpan.FromMilliseconds(10) };
        var data = TestData.RandomBytes(600_000, 8);
        var entry = TestData.Entry("m", ("m.gguf", data));
        Serve(server, entry, data);
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        using var http = server.CreateClient();
        var downloader = new ModelDownloader(http, store, new FakeDisk(), Fast());

        var first = downloader.DownloadAsync(entry, null, Ct);
        var joined = downloader.DownloadAsync(entry, null, Ct);
        while (server.Requests.Count == 0)
        {
            await Task.Delay(10);
        }

        var other = new ModelDownloader(http, store, new FakeDisk(), Fast());
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.DownloadAsync(entry, null, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RemoveAsync("m", Ct));

        var a = await first;
        var b = await joined;
        Assert.Equal(a.Files[0].Path, b.Files[0].Path);
        Assert.Single(server.Requests);
        Assert.Equal(data, File.ReadAllBytes(a.Files[0].Path));
    }

    // ---- 5: resume only from bytes that were flushed ----

    [Fact]
    public async Task ResumeTruncatesToTheFlushedCheckpoint()
    {
        using var root = new TestRoot();
        await using var server = new RangeServer();
        var data = TestData.RandomBytes(800_000, 9);
        var entry = TestData.Entry("m", ("m.gguf", data));
        Serve(server, entry, data);
        server.DropAfter = n => n == 1 ? 500_000 : null;
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        using var http = server.CreateClient();
        await Assert.ThrowsAsync<DownloadException>(() =>
            new ModelDownloader(http, store, new FakeDisk(), Fast(maxAttempts: 1)).DownloadAsync(entry, null, Ct));

        // Simulate a power cut: the sidecar only vouches for the first 100,000 bytes; the rest is garbage.
        var part = (await store.GetAsync("m", Ct))!.Files[0].Path;
        var sidecar = part + ".json";
        var json = File.ReadAllText(sidecar);
        var done = json[(json.IndexOf("\"bytes_done\":", StringComparison.Ordinal) + 13)..].TrimEnd('}', ' ');
        File.WriteAllText(sidecar, json.Replace("\"bytes_done\":" + done, "\"bytes_done\":100000", StringComparison.Ordinal));
        using (var fs = new FileStream(part, FileMode.Open, FileAccess.Write))
        {
            fs.Seek(150_000, SeekOrigin.Begin);
            fs.Write(new byte[1000]);
        }

        var model = await new ModelDownloader(http, store, new FakeDisk(), Fast()).DownloadAsync(entry, null, Ct);
        Assert.Equal("bytes=100000-", server.Requests[^1].Range);
        Assert.Equal(data, File.ReadAllBytes(model.Files[0].Path));
    }

    [Fact]
    public async Task PartWithoutSidecarStartsFromZero()
    {
        using var root = new TestRoot();
        await using var server = new RangeServer();
        var data = TestData.RandomBytes(300_000, 10);
        var entry = TestData.Entry("m", ("m.gguf", data));
        Serve(server, entry, data);
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        await store.EnsureModelAsync("m", "m", "chat", "llama-server", Ct);
        var part = await store.BeginFileAsync("m", "m.gguf", Ct);
        await File.WriteAllBytesAsync(part, new byte[50_000], Ct); // unflushed garbage, no sidecar
        using var http = server.CreateClient();

        var model = await new ModelDownloader(http, store, new FakeDisk(), Fast()).DownloadAsync(entry, null, Ct);
        Assert.Null(server.Requests[0].Range);
        Assert.Equal(data, File.ReadAllBytes(model.Files[0].Path));
    }

    [Fact]
    public async Task BadContentRangeIsRefusedAndPartUntouched()
    {
        using var root = new TestRoot();
        await using var server = new RangeServer();
        var data = TestData.RandomBytes(600_000, 11);
        var entry = TestData.Entry("m", ("m.gguf", data));
        Serve(server, entry, data);
        server.DropAfter = n => n == 1 ? 200_000 : null;
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        using var http = server.CreateClient();
        await Assert.ThrowsAsync<DownloadException>(() =>
            new ModelDownloader(http, store, new FakeDisk(), Fast(maxAttempts: 1)).DownloadAsync(entry, null, Ct));
        var part = (await store.GetAsync("m", Ct))!.Files[0].Path;
        var before = File.ReadAllBytes(part);

        server.ContentRangeShift = 1000;
        await Assert.ThrowsAsync<DownloadException>(() =>
            new ModelDownloader(http, store, new FakeDisk(), Fast()).DownloadAsync(entry, null, Ct));
        Assert.Equal(206, server.Requests[^1].Status);
        Assert.Equal(before, File.ReadAllBytes(part));
    }

    // ---- 7: hard links and replaced parts are never written ----

    [Fact]
    public async Task HardLinkedPartIsNeverWritten()
    {
        using var root = new TestRoot();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        var part = await store.BeginFileAsync("m", "a.gguf", Ct);
        await File.WriteAllBytesAsync(part, Encoding.UTF8.GetBytes("shared data"), Ct);
        var alias = Path.Combine(root.Sub("users"), "alias.gguf");
        TestRoot.CreateHardLink(alias, part);

        await Assert.ThrowsAsync<StoreSafetyException>(() => store.ResetPartAsync("m", part, Ct));
        await Assert.ThrowsAsync<StoreSafetyException>(() => store.TruncatePartAsync("m", part, 1, Ct));
        await Assert.ThrowsAsync<StoreSafetyException>(() => store.OpenPartForWriteAsync("m", part, 4096, Ct));
        Assert.Equal("shared data", File.ReadAllText(alias));
    }

    [Fact]
    public async Task PartReplacedByLinkToUserFileIsNeverWritten()
    {
        using var root = new TestRoot();
        var victim = Path.Combine(root.Sub("users"), "victim.gguf");
        File.WriteAllText(victim, "precious");
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        var part = await store.BeginFileAsync("m", "a.gguf", Ct);
        File.Delete(part);
        TestRoot.CreateHardLink(part, victim);

        await Assert.ThrowsAsync<StoreSafetyException>(() => store.ResetPartAsync("m", part, Ct));
        Assert.Equal("precious", File.ReadAllText(victim));
    }

    [Fact]
    public async Task PartReplacedByAnotherFileIsNeverWritten()
    {
        using var root = new TestRoot();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        var part = await store.BeginFileAsync("m", "a.gguf", Ct);
        File.Delete(part);
        File.WriteAllText(part, "someone else's file now");

        await Assert.ThrowsAsync<StoreSafetyException>(() => store.ResetPartAsync("m", part, Ct));
        Assert.Equal("someone else's file now", File.ReadAllText(part));
    }

    // ---- 8: old-copy removal never deletes the only copies ----

    private static async Task<StoredFile> AddFileAsync(ModelStore store, string modelId, string name, byte[] data)
    {
        var part = await store.BeginFileAsync(modelId, name, Ct);
        await File.WriteAllBytesAsync(part, data, Ct);
        return await store.CommitFileAsync(modelId, part, TestData.Sha(data), Ct);
    }

    [Fact]
    public async Task PreviousStoreThatIsTheCurrentStoreIsRefused()
    {
        using var root = new TestRoot();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        var oldPath = store.Location.Path;
        var oldId = store.Location.StoreId;
        await AddFileAsync(store, "m", "a.gguf", TestData.RandomBytes(1000, 12));
        await store.MoveStoreAsync(root.Sub("new"), null, Ct);
        var json = Path.Combine(store.Location.Path, "store.json");
        File.WriteAllText(json, File.ReadAllText(json)
            .Replace(oldPath.Replace("\\", "\\\\", StringComparison.Ordinal), store.Location.Path.Replace("\\", "\\\\", StringComparison.Ordinal), StringComparison.Ordinal)
            .Replace(oldId.ToString(), store.Location.StoreId.ToString(), StringComparison.Ordinal));
        var curPath = store.Location.Path;
        store.Dispose();
        var reopened = ModelStore.Open(curPath, new FakeDisk());
        var copy = (await reopened.GetAsync("m", Ct))!.Files[0].Path;

        await Assert.ThrowsAsync<StoreSafetyException>(() => reopened.RemoveOldCopiesAsync(Ct));
        Assert.True(File.Exists(copy));
    }

    [Fact]
    public async Task OldCopyIsKeptWhenTheNewCopyFailsItsHash()
    {
        using var root = new TestRoot();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        var a = await AddFileAsync(store, "m", "a.gguf", TestData.RandomBytes(1000, 13));
        await store.MoveStoreAsync(root.Sub("new"), null, Ct);
        var copy = (await store.GetAsync("m", Ct))!.Files[0].Path;
        var bytes = File.ReadAllBytes(copy);
        bytes[5] ^= 0xFF; // same length, damaged
        File.WriteAllBytes(copy, bytes);

        var kept = await store.RemoveOldCopiesAsync(Ct);
        Assert.Single(kept);
        Assert.True(File.Exists(a.Path));
    }

    // ---- 10: store errors ----

    [Fact]
    public void DamagedStoreJsonIsRefusedAndLeftInPlace()
    {
        using var root = new TestRoot();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        var json = Path.Combine(store.Location.Path, "store.json");
        File.WriteAllText(json, "{ not json");
        store.Dispose();

        Assert.Throws<StoreRefusedException>(() => ModelStore.Open(store.Location.Path, new FakeDisk()));
        Assert.Equal("{ not json", File.ReadAllText(json));
    }

    [Fact]
    public async Task FailedMoveReportsTheOriginalErrorEvenIfCleanupFails()
    {
        using var root = new TestRoot();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        await AddFileAsync(store, "m", "m-00001-of-00002.gguf", TestData.RandomBytes(1000, 14));
        var second = await AddFileAsync(store, "m", "m-00002-of-00002.gguf", TestData.RandomBytes(1000, 15));
        var parent = root.Sub("new");
        var newMarker = Path.Combine(parent, ModelStore.UserFolderName, ".portal-ai-host-store");
        var progress = new SyncProgress<double>(p =>
        {
            if (p < 1 && File.Exists(newMarker))
            {
                File.Delete(newMarker); // makes the cleanup refuse
                var b = File.ReadAllBytes(second.Path);
                b[0] ^= 0xFF; // makes the next copy fail verification
                File.WriteAllBytes(second.Path, b);
            }
        });

        var ex = await Assert.ThrowsAsync<IOException>(() => store.MoveStoreAsync(parent, progress, Ct));
        Assert.Contains("did not verify", ex.Message, StringComparison.Ordinal);
    }

    // ---- 9 + reserved names ----

    [Theory]
    [InlineData("con.gguf")]
    [InlineData("NUL.gguf")]
    [InlineData("com1.part.gguf")]
    [InlineData("lpt9.gguf")]
    public async Task ReservedDeviceNamesAreRefusedByTheStore(string name)
    {
        using var root = new TestRoot();
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        await Assert.ThrowsAsync<StoreSafetyException>(() => store.BeginFileAsync("m", name, Ct));
        await Assert.ThrowsAsync<StoreSafetyException>(() => store.BeginFileAsync("aux", "ok.gguf", Ct));
    }

    [Theory]
    [InlineData("""{"schema_version":1,"catalogue_version":1,"models":null}""")]
    [InlineData("""{"schema_version":1,"catalogue_version":1,"models":[null]}""")]
    [InlineData("""{"schema_version":1,"catalogue_version":1,"models":[{"id":"x","display_name":"x","licence":null,"memory":null,"files":null,"params_total_b":1,"params_active_b":1,"context_max":1,"context_default":1}]}""")]
    [InlineData("""{"schema_version":1,"catalogue_version":1,"models":[{"id":null,"hf_repo":null,"hf_commit":null,"files":[null]}]}""")]
    [InlineData("""{"schema_version":1,"catalogue_version":1,"models":[{"id":"x","files":[{"name":"a.gguf","size":1,"sha256":"x","urls":null}]}]}""")]
    [InlineData("""{"schema_version":1,"catalogue_version":1,"models":[{"id":"x","files":[{"name":null,"size":1,"sha256":null,"urls":[null]}],"features":null}]}""")]
    public void NullFieldsAreInvalidNotACrash(string json)
    {
        var (catalogue, errors) = CatalogueValidator.Parse(Encoding.UTF8.GetBytes(json));
        Assert.Null(catalogue);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void RepoCommitAndReservedNamesAreChecked()
    {
        CatalogueEntry Make(string id, string file, string repo = "test/repo", string commit = TestData.Commit)
        {
            var e = TestData.Entry(id, (file, new byte[10]));
            return new CatalogueEntry
            {
                Id = e.Id, DisplayName = e.DisplayName, HfRepo = repo, HfCommit = commit, ParamsTotalB = 1, ParamsActiveB = 1,
                ContextDefault = 1, ContextMax = 1, Licence = e.Licence, Memory = e.Memory,
                Files = [new CatalogueFile { Name = file, Size = 10, Sha256 = e.Files[0].Sha256, Urls = [$"https://huggingface.co/{repo}/resolve/{commit}/{file}"] }],
            };
        }

        List<string> Check(CatalogueEntry e) => CatalogueValidator.Validate(new ModelCatalogue { CatalogueVersion = 1, Models = { e } });

        Assert.Empty(Check(Make("x", "x.gguf")));
        Assert.Contains(Check(Make("x", "x.gguf", repo: "../evil")), e => e.Contains("hf_repo", StringComparison.Ordinal));
        Assert.Contains(Check(Make("x", "x.gguf", repo: "just-a-name")), e => e.Contains("hf_repo", StringComparison.Ordinal));
        Assert.Contains(Check(Make("x", "x.gguf", commit: "main")), e => e.Contains("hf_commit must be", StringComparison.Ordinal));
        Assert.Contains(Check(Make("x", "nul.gguf")), e => e.Contains("unsafe file name", StringComparison.Ordinal));
        Assert.Contains(Check(Make("x", "com3.q4.gguf")), e => e.Contains("unsafe file name", StringComparison.Ordinal));
        Assert.Contains(Check(Make("lpt1", "x.gguf")), e => e.Contains("reserved", StringComparison.Ordinal));
    }

    // ---- 6: planner never assumes free memory it does not know about ----

    private static HardwareProfile Rx6600Pc => new("windows", "x64", 12, 64 * GiB,
        [new GpuProfile("AMD Radeon RX 6600", "amd", "0x1", "31.0", 8 * GiB, 0)], [], "fp");

    [Fact]
    public void UnknownFreeRamUsesTheConservativeFigure()
    {
        var s = new PlannerSettings();
        var unknown = MemoryBudget.From(Rx6600Pc, new MemorySnapshot(64 * GiB, 0, [(long)(7.5 * GiB)]), s);
        var allFree = MemoryBudget.From(Rx6600Pc, new MemorySnapshot(64 * GiB, 64 * GiB, [(long)(7.5 * GiB)]), s);
        Assert.True(unknown.RamBudgetBytes < allFree.RamBudgetBytes);
        Assert.Equal(48 * GiB - (long)(64 * GiB * 0.15), unknown.RamBudgetBytes);
    }

    [Fact]
    public void UnknownFreeVramIsNotAllFree()
    {
        var budget = MemoryBudget.From(Rx6600Pc, new MemorySnapshot(64 * GiB, 50 * GiB, []), new PlannerSettings());
        Assert.True(budget.VramBudgetBytes <= (long)(8 * GiB * 0.8) - (long)(8 * GiB * 0.1));
        var zero = MemoryBudget.From(Rx6600Pc, new MemorySnapshot(64 * GiB, 50 * GiB, [0]), new PlannerSettings());
        Assert.Equal(budget.VramBudgetBytes, zero.VramBudgetBytes);
    }

    [Fact]
    public void MemoryHeldByRunningEnginesIsSubtracted()
    {
        var model = CatalogueValidator.Parse(TestData.SeedCatalogueBytes()).Catalogue!.Models.Single(m => m.Id == "qwen3-30b-a3b-instruct-2507-q4km");
        var mem = new MemorySnapshot(64 * GiB, 50 * GiB, [(long)(7.5 * GiB)]);
        var free = PlacementPlanner.Plan(model, Rx6600Pc, mem);
        var busy = PlacementPlanner.Plan(model, Rx6600Pc, mem, committed: new CommittedMemory(10 * GiB, [2 * GiB]));
        Assert.True(busy.Placement!.CpuMoeLayers > free.Placement!.CpuMoeLayers);

        var s = new PlannerSettings();
        var idle = new MemorySnapshot(64 * GiB, 60 * GiB, [(long)(7.5 * GiB)]); // here the max-memory cap is the limit
        var b1 = MemoryBudget.From(Rx6600Pc, idle, s);
        var b2 = MemoryBudget.From(Rx6600Pc, idle, s, new CommittedMemory(10 * GiB, [2 * GiB]));
        Assert.Equal(b1.VramBudgetBytes - 2 * GiB, b2.VramBudgetBytes);
        Assert.True(b2.RamBudgetBytes < b1.RamBudgetBytes);
    }
}
