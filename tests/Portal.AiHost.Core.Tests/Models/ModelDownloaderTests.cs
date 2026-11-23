using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Catalogue;
using Portal.AiHost.Core.Download;
using Portal.AiHost.Core.Store;

namespace Portal.AiHost.Core.Tests.Models;

public class ModelDownloaderTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static DownloadOptions Fast(int? maxAttempts = 3) => new()
    {
        UserAgent = "PortalAIHost/1.2.3",
        RetryDelays = [TimeSpan.Zero, TimeSpan.Zero],
        RetryEvery = TimeSpan.Zero,
        MaxAttempts = maxAttempts,
        BufferBytes = 64 * 1024,
        DiskCheckIntervalBytes = 256 * 1024,
        FixedHeadroomBytes = 0,
    };

    private static void Serve(RangeServer server, CatalogueEntry entry, params byte[][] data)
    {
        for (var i = 0; i < entry.Files.Count; i++)
        {
            server.Files[new Uri(entry.Files[i].Urls[0]).AbsolutePath] = data[i];
        }
    }

    [Fact]
    public async Task DownloadsVerifiesAndCommitsWithUserAgent()
    {
        using var root = new TestRoot();
        await using var server = new RangeServer();
        var data = TestData.RandomBytes(700_000, 1);
        var entry = TestData.Entry("tiny", ("tiny.gguf", data));
        Serve(server, entry, data);
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        using var http = server.CreateClient();

        var model = await new ModelDownloader(http, store, new FakeDisk(), Fast()).DownloadAsync(entry, null, Ct);

        Assert.True(model.IsComplete);
        Assert.Equal(data, File.ReadAllBytes(model.Files[0].Path));
        Assert.Equal("tiny.gguf", Path.GetFileName(model.Files[0].Path));
        Assert.All(server.Requests, r => Assert.Equal("PortalAIHost/1.2.3", r.UserAgent));
        Assert.Empty(Directory.GetFiles(store.Location.Path, "*.part*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SplitPartsKeepTheirNames()
    {
        using var root = new TestRoot();
        await using var server = new RangeServer();
        var parts = Enumerable.Range(1, 3).Select(i => TestData.RandomBytes(300_000 + i, 10 + i)).ToArray();
        var entry = TestData.Entry("big", parts.Select((d, i) => ($"big-{i + 1:00000}-of-00003.gguf", d)).ToArray());
        Serve(server, entry, parts);
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        using var http = server.CreateClient();
        var progress = new List<DownloadProgress>();

        var model = await new ModelDownloader(http, store, new FakeDisk(), Fast())
            .DownloadAsync(entry, new SyncProgress(progress), Ct);

        Assert.Equal(["big-00001-of-00003.gguf", "big-00002-of-00003.gguf", "big-00003-of-00003.gguf"],
            model.Files.Select(f => Path.GetFileName(f.Path)).Order());
        Assert.Equal("done", progress[^1].State);
        Assert.Equal(entry.TotalBytes, progress[^1].BytesDone);
    }

    [Fact]
    public async Task ResumesAfterRestartWithRangeAndIfRange()
    {
        using var root = new TestRoot();
        await using var server = new RangeServer();
        var data = TestData.RandomBytes(1_000_000, 2);
        var entry = TestData.Entry("tiny", ("tiny.gguf", data));
        Serve(server, entry, data);
        server.DropAfter = n => n == 1 ? 400_000 : null;
        var folder = Path.Combine(root.Path, "models");
        using var http = server.CreateClient();

        // First run: the connection drops and the run gives up (as if the app closed).
        var first = ModelStore.OpenOrCreate(folder, new FakeDisk());
        await Assert.ThrowsAsync<DownloadException>(() =>
            new ModelDownloader(http, first, new FakeDisk(), Fast(maxAttempts: 1)).DownloadAsync(entry, null, Ct));
        var partial = (await first.GetAsync("tiny", Ct))!.Files[0];
        Assert.Equal(StoredFileState.Partial, partial.State);
        Assert.InRange(partial.SizeBytes, 1, data.Length - 1);

        // "Restart": a new store and downloader instance pick up the .part and its sidecar.
        first.Dispose();
        var second = ModelStore.Open(folder, new FakeDisk());
        var model = await new ModelDownloader(http, second, new FakeDisk(), Fast()).DownloadAsync(entry, null, Ct);

        Assert.Equal(data, File.ReadAllBytes(model.Files[0].Path));
        var resume = server.Requests[^1];
        Assert.Equal($"bytes={partial.SizeBytes}-", resume.Range);
        Assert.Equal("\"v1\"", resume.IfRange);
        Assert.Equal(206, resume.Status);
        Assert.True(server.BodyBytesSent < data.Length * 2 - partial.SizeBytes + 1);
    }

    [Fact]
    public async Task RetriesThroughDroppedConnections()
    {
        using var root = new TestRoot();
        await using var server = new RangeServer();
        var data = TestData.RandomBytes(900_000, 3);
        var entry = TestData.Entry("tiny", ("tiny.gguf", data));
        Serve(server, entry, data);
        server.DropAfter = n => n <= 2 ? 200_000 : null;
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        using var http = server.CreateClient();

        var model = await new ModelDownloader(http, store, new FakeDisk(), Fast(maxAttempts: 5)).DownloadAsync(entry, null, Ct);
        Assert.Equal(data, File.ReadAllBytes(model.Files[0].Path));
        Assert.Equal(3, server.Requests.Count);
        Assert.All(server.Requests.Skip(1), r => Assert.StartsWith("bytes=", r.Range));
    }

    [Fact]
    public async Task ChangedResourceRestartsFromZero()
    {
        using var root = new TestRoot();
        await using var server = new RangeServer();
        var data = TestData.RandomBytes(600_000, 4);
        var entry = TestData.Entry("tiny", ("tiny.gguf", data));
        Serve(server, entry, data);
        server.DropAfter = n => n == 1 ? 100_000 : null;
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        using var http = server.CreateClient();
        await Assert.ThrowsAsync<DownloadException>(() =>
            new ModelDownloader(http, store, new FakeDisk(), Fast(maxAttempts: 1)).DownloadAsync(entry, null, Ct));

        server.IgnoreRange = true; // server answers 200 with the full body
        var model = await new ModelDownloader(http, store, new FakeDisk(), Fast()).DownloadAsync(entry, null, Ct);
        Assert.Equal(data, File.ReadAllBytes(model.Files[0].Path));
        Assert.Equal(200, server.Requests[^1].Status);
    }

    [Fact]
    public async Task HashMismatchRetriesOnceThenReportsDamagedAndLeavesNothing()
    {
        using var root = new TestRoot();
        await using var server = new RangeServer();
        var data = TestData.RandomBytes(300_000, 5);
        var entry = TestData.Entry("tiny", ("tiny.gguf", data));
        var corrupt = (byte[])data.Clone();
        corrupt[1234] ^= 0x55;
        Serve(server, entry, corrupt);
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        var foreign = Path.Combine(store.Location.Path, "users.txt");
        File.WriteAllText(foreign, "keep");
        using var http = server.CreateClient();

        await Assert.ThrowsAsync<ModelFileDamagedException>(() =>
            new ModelDownloader(http, store, new FakeDisk(), Fast()).DownloadAsync(entry, null, Ct));

        Assert.Equal(2, server.Requests.Count);
        Assert.Null(await store.GetAsync("tiny", Ct));
        Assert.Empty(Directory.GetFiles(store.Location.Path, "*.gguf*", SearchOption.AllDirectories));
        Assert.Equal("keep", File.ReadAllText(foreign));
    }

    [Fact]
    public async Task NotEnoughDiskSpaceStopsBeforeAnyRequest()
    {
        using var root = new TestRoot();
        await using var server = new RangeServer();
        var data = TestData.RandomBytes(100_000, 6);
        var entry = TestData.Entry("tiny", ("tiny.gguf", data));
        Serve(server, entry, data);
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        using var http = server.CreateClient();
        var opts = new DownloadOptions { FixedHeadroomBytes = 2L << 30 };

        var ex = await Assert.ThrowsAsync<InsufficientDiskSpaceException>(() =>
            new ModelDownloader(http, store, new FakeDisk(freeBytes: 1L << 30), opts).DownloadAsync(entry, null, Ct));
        Assert.True(ex.NeededBytes > (2L << 30));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task DiskFillingUpDuringDownloadPausesIt()
    {
        using var root = new TestRoot();
        await using var server = new RangeServer();
        var data = TestData.RandomBytes(2_000_000, 7);
        var entry = TestData.Entry("tiny", ("tiny.gguf", data));
        Serve(server, entry, data);
        var disk = new FakeDisk(freeBytes: 10_000_000);
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), disk);
        using var http = server.CreateClient();
        server.ChunkDelay = TimeSpan.FromMilliseconds(5);
        var progress = new Progress<DownloadProgress>(p =>
        {
            if (p.BytesDone > 300_000)
            {
                disk.FreeBytes = 1000;
            }
        });

        await Assert.ThrowsAsync<InsufficientDiskSpaceException>(() =>
            new ModelDownloader(http, store, disk, Fast()).DownloadAsync(entry, progress, Ct));
        Assert.Equal(StoredFileState.Partial, (await store.GetAsync("tiny", Ct))!.Files[0].State);
    }

    [Fact]
    public async Task CancelTokenPausesAndCancelAsyncDiscardsOnlyHostFiles()
    {
        using var root = new TestRoot();
        await using var server = new RangeServer { ChunkDelay = TimeSpan.FromMilliseconds(20) };
        var data = TestData.RandomBytes(2_000_000, 8);
        var entry = TestData.Entry("tiny", ("tiny.gguf", data));
        Serve(server, entry, data);
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        using var http = server.CreateClient();
        var downloader = new ModelDownloader(http, store, new FakeDisk(), Fast());
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(null, p =>
        {
            if (p.BytesDone > 200_000)
            {
                cts.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloader.DownloadAsync(entry, progress, cts.Token));
        var partial = (await store.GetAsync("tiny", Ct))!.Files[0];
        Assert.Equal(StoredFileState.Partial, partial.State);
        Assert.True(File.Exists(partial.Path), "pause keeps the .part for resume");
        var foreign = Path.Combine(Path.GetDirectoryName(partial.Path)!, "notes.txt");
        File.WriteAllText(foreign, "keep");

        await downloader.CancelAsync("tiny", Ct);
        Assert.False(File.Exists(partial.Path));
        Assert.Equal("keep", File.ReadAllText(foreign));
        Assert.Null(await store.GetAsync("tiny", Ct));
    }

    [Fact]
    public async Task CancelAsyncRefusesFinishedModels()
    {
        using var root = new TestRoot();
        await using var server = new RangeServer();
        var data = TestData.RandomBytes(10_000, 9);
        var entry = TestData.Entry("tiny", ("tiny.gguf", data));
        Serve(server, entry, data);
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        using var http = server.CreateClient();
        var downloader = new ModelDownloader(http, store, new FakeDisk(), Fast());
        var model = await downloader.DownloadAsync(entry, null, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => downloader.CancelAsync("tiny", Ct));
        Assert.True(File.Exists(model.Files[0].Path));
    }

    [Fact]
    public async Task SizeMismatchAtSourceIsRefused()
    {
        using var root = new TestRoot();
        await using var server = new RangeServer();
        var data = TestData.RandomBytes(50_000, 10);
        var entry = TestData.Entry("tiny", ("tiny.gguf", data));
        Serve(server, entry, TestData.RandomBytes(60_000, 10));
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        using var http = server.CreateClient();

        await Assert.ThrowsAsync<DownloadException>(() =>
            new ModelDownloader(http, store, new FakeDisk(), Fast()).DownloadAsync(entry, null, Ct));
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task PlaceholderCatalogueEntriesAreNeverDownloaded()
    {
        using var root = new TestRoot();
        await using var server = new RangeServer();
        var unpinned = System.Text.RegularExpressions.Regex.Replace(
            System.Text.Encoding.UTF8.GetString(TestData.SeedCatalogueBytes()),
            "\"sha256\": \"[0-9a-f]{64}\"", "\"sha256\": \"PLACEHOLDER-UNVERIFIED-SHA256\"");
        var (catalogue, _) = CatalogueValidator.Parse(System.Text.Encoding.UTF8.GetBytes(unpinned));
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());
        using var http = server.CreateClient();
        var downloader = new ModelDownloader(http, store, new FakeDisk(), Fast());

        foreach (var entry in catalogue!.Models)
        {
            await Assert.ThrowsAsync<NotDownloadableException>(() => downloader.DownloadAsync(entry, null, Ct));
        }

        Assert.Empty(server.Requests);
        Assert.Empty(await store.ListAsync(Ct));
    }

    /// <summary>Reports synchronously on the calling thread (Progress&lt;T&gt; posts to the thread pool).</summary>
    private sealed class SyncProgress(List<DownloadProgress>? sink, Action<DownloadProgress>? onReport = null) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value)
        {
            lock (this)
            {
                sink?.Add(value);
            }

            onReport?.Invoke(value);
        }
    }
}
