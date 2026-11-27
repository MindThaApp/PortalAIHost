using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Portal.AiHost.Core.Store;
using Portal.AiHost.Engine.Flavours;

namespace Portal.AiHost.Engine.Tests;

public sealed class FlavourTests : IDisposable
{
    private const string Id = "llama-cuda12";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pah-flavour-tests", Guid.NewGuid().ToString("N"));
    private readonly FlavourStore _store;

    public FlavourTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new FlavourStore(Path.Combine(_dir, "engines"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true); // the test's own temp folder
        }
        catch (IOException)
        {
        }
    }

    private static readonly Dictionary<string, byte[]> DefaultFiles = new()
    {
        ["llama-server.exe"] = Encoding.UTF8.GetBytes(new string('x', 5000)),
        ["ggml-cuda.dll"] = Encoding.UTF8.GetBytes(new string('y', 3000)),
        ["licenses/LICENSE"] = Encoding.UTF8.GetBytes("MIT"),
    };

    private static byte[] Zip(IReadOnlyDictionary<string, byte[]> files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in files)
            {
                using var s = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                s.Write(bytes);
            }
        }

        return ms.ToArray();
    }

    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    private static FlavourEntry Entry(byte[] zip, IReadOnlyDictionary<string, byte[]>? files = null, string tag = "b100",
        string? sha = null, long? size = null, List<string>? urls = null) => new()
    {
        Id = Id,
        Engine = "llama",
        Tag = tag,
        Asset = "llama-cuda.zip",
        Urls = urls ?? ["https://upstream.test/llama-cuda.zip"],
        Sha256 = sha ?? Sha(zip),
        Size = size ?? zip.Length,
        UnpackedSize = (files ?? DefaultFiles).Values.Sum(v => (long)v.Length) + 10,
        Files = [.. (files ?? DefaultFiles).Select(kv => new FlavourFile { Path = kv.Key, Sha256 = Sha(kv.Value), Size = kv.Value.Length })],
        Requires = ["cudart12"],
    };

    private FlavourDownloader Downloader(ZipServer server, int attempts = 1, IDiskInfo? disk = null) =>
        new(new HttpClient(server), _store, disk ?? new BigDisk(),
            new FlavourDownloadOptions { AttemptsPerUrl = attempts, BufferBytes = 4096, Delay = (_, _) => Task.CompletedTask });

    private string[] StagingFolders() =>
        Directory.Exists(_store.FlavourFolder(Id)) ? Directory.GetDirectories(_store.FlavourFolder(Id), "*" + FlavourStore.StagingSuffix) : [];

    [Fact]
    public async Task InstallsVerifiesAndRemovesThePart()
    {
        var zip = Zip(DefaultFiles);
        var e = Entry(zip);
        var progress = new List<string>();
        var folder = await Downloader(new ZipServer(zip)).InstallAsync(e, new SyncProgress(p => progress.Add(p.State)), CancellationToken.None);

        Assert.Equal(_store.VersionFolder(e), folder);
        Assert.True(_store.Verify(folder, e).Ok);
        Assert.Equal(_store.VersionFolder(e), _store.TryGetVerified(e));
        Assert.True(File.Exists(Path.Combine(folder, "licenses", "LICENSE")));
        Assert.False(File.Exists(_store.PartPath(e)));
        Assert.Empty(StagingFolders());
        Assert.Contains("downloading", progress);
        Assert.Equal("installed", progress[^1]);
    }

    [Fact]
    public async Task HashMismatchRefusesAndDeletesThePart()
    {
        var zip = Zip(DefaultFiles);
        var e = Entry(zip, sha: Sha(Encoding.UTF8.GetBytes("something else")));
        var ex = await Assert.ThrowsAsync<FlavourInstallException>(() => Downloader(new ZipServer(zip)).InstallAsync(e, null, CancellationToken.None));

        Assert.Equal("hash_mismatch", ex.Code);
        Assert.False(File.Exists(_store.PartPath(e)));
        Assert.False(Directory.Exists(_store.VersionFolder(e)));
        Assert.Empty(StagingFolders());
    }

    [Fact]
    public async Task SizeMismatchRefuses()
    {
        var zip = Zip(DefaultFiles);
        var e = Entry(zip, size: zip.Length + 7);
        var ex = await Assert.ThrowsAsync<FlavourInstallException>(() => Downloader(new ZipServer(zip)).InstallAsync(e, null, CancellationToken.None));

        Assert.Equal("size_mismatch", ex.Code);
        Assert.False(File.Exists(_store.PartPath(e)));
        Assert.False(Directory.Exists(_store.VersionFolder(e)));
    }

    [Fact]
    public async Task OversizedBodyRefusesEvenWithAMatchingHeader()
    {
        var zip = Zip(DefaultFiles);
        var e = Entry(zip, size: zip.Length - 100);
        var server = new ZipServer(zip) { AdvertisedLength = zip.Length - 100 };
        var ex = await Assert.ThrowsAsync<FlavourInstallException>(() => Downloader(server).InstallAsync(e, null, CancellationToken.None));

        Assert.Equal("size_mismatch", ex.Code);
        Assert.False(File.Exists(_store.PartPath(e)));
    }

    [Fact]
    public async Task InterruptedDownloadResumesFromThePart()
    {
        var zip = Zip(DefaultFiles);
        var e = Entry(zip);
        var server = new ZipServer(zip) { CutAfter = 3000 };
        var ex = await Assert.ThrowsAsync<FlavourInstallException>(() => Downloader(server).InstallAsync(e, null, CancellationToken.None));
        Assert.Equal("download_failed", ex.Code);
        var kept = new FileInfo(_store.PartPath(e)).Length;
        Assert.InRange(kept, 1L, zip.Length - 1L);

        var folder = await Downloader(server).InstallAsync(e, null, CancellationToken.None);

        Assert.Equal(kept, server.RangeStarts[^1]);
        Assert.True(_store.Verify(folder, e).Ok);
        Assert.False(File.Exists(_store.PartPath(e)));
    }

    [Fact]
    public async Task RetriesResumeWithinOneInstall()
    {
        var zip = Zip(DefaultFiles);
        var e = Entry(zip);
        var server = new ZipServer(zip) { CutAfter = 2000 };
        var folder = await Downloader(server, attempts: 2).InstallAsync(e, null, CancellationToken.None);

        Assert.Equal(new long?[] { null, 2000L }, server.RangeStarts);
        Assert.True(_store.Verify(folder, e).Ok);
    }

    [Fact]
    public async Task FallsBackToTheSecondUrl()
    {
        var zip = Zip(DefaultFiles);
        var e = Entry(zip, urls: ["https://upstream.test/a.zip", "https://mirror.test/a.zip"]);
        var server = new ZipServer(zip) { Missing = { "upstream.test" } };
        var folder = await Downloader(server, attempts: 3).InstallAsync(e, null, CancellationToken.None);

        Assert.True(_store.Verify(folder, e).Ok);
        Assert.Equal(new[] { "upstream.test", "mirror.test" }, server.Hosts);
    }

    [Fact]
    public async Task NotEnoughDiskSpaceRefusesBeforeDownloading()
    {
        var zip = Zip(DefaultFiles);
        var e = Entry(zip);
        var server = new ZipServer(zip);
        var ex = await Assert.ThrowsAsync<FlavourInstallException>(() => Downloader(server, disk: new BigDisk(10)).InstallAsync(e, null, CancellationToken.None));

        Assert.Equal("disk_space", ex.Code);
        Assert.Empty(server.Hosts);
    }

    [Fact]
    public async Task FileNotMatchingTheListCleansStaging()
    {
        var zip = Zip(DefaultFiles);
        var listed = new Dictionary<string, byte[]>(DefaultFiles) { ["ggml-cuda.dll"] = Encoding.UTF8.GetBytes(new string('z', 3000)) };
        var e = Entry(zip, files: listed);
        var ex = await Assert.ThrowsAsync<FlavourInstallException>(() => Downloader(new ZipServer(zip)).InstallAsync(e, null, CancellationToken.None));

        Assert.Equal("bad_archive", ex.Code);
        Assert.Empty(StagingFolders());
        Assert.False(Directory.Exists(_store.VersionFolder(e)));
    }

    [Fact]
    public async Task ZipSlipEntryIsRefusedAndNothingLandsOutside()
    {
        var files = new Dictionary<string, byte[]>(DefaultFiles) { ["../evil.dll"] = [1, 2, 3] };
        var zip = Zip(files);
        var e = Entry(zip); // the pinned list does not hold the extra entry
        var ex = await Assert.ThrowsAsync<FlavourInstallException>(() => Downloader(new ZipServer(zip)).InstallAsync(e, null, CancellationToken.None));

        Assert.Equal("unsafe", ex.Code);
        Assert.False(File.Exists(Path.Combine(_store.FlavourFolder(Id), "evil.dll")));
        Assert.Empty(StagingFolders());
    }

    [Fact]
    public void CleanStagingRemovesOnlyMarkedFolders()
    {
        var e = Entry(Zip(DefaultFiles));
        var marked = _store.CreateStaging(e);
        File.WriteAllText(Path.Combine(marked, "llama-server.exe"), "half");
        var unmarked = Path.Combine(_store.FlavourFolder(Id), "someone-else" + FlavourStore.StagingSuffix);
        Directory.CreateDirectory(unmarked);
        File.WriteAllText(Path.Combine(unmarked, "keep.txt"), "keep");

        Assert.Equal(1, _store.CleanStaging(Id));
        Assert.False(Directory.Exists(marked));
        Assert.True(File.Exists(Path.Combine(unmarked, "keep.txt")));
    }

    [Fact]
    public async Task MarkerlessFolderIsNeverDeleted()
    {
        var zip = Zip(DefaultFiles);
        var e = Entry(zip);
        await Downloader(new ZipServer(zip)).InstallAsync(e, null, CancellationToken.None);
        var foreign = Path.Combine(_store.FlavourFolder(Id), "b001-12345678");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "llama-server.exe"), "user file");

        Assert.Empty(_store.RemoveOldVersions(e, _ => false));
        Assert.Throws<FlavourSafetyException>(() => _store.DeleteOwnedFolder(foreign, Id));
        Assert.True(File.Exists(Path.Combine(foreign, "llama-server.exe")));
    }

    [Fact]
    public void FolderOutsideTheStoreIsNeverDeleted()
    {
        var e = Entry(Zip(DefaultFiles));
        var staging = _store.CreateStaging(e);
        var outside = Path.Combine(_dir, "outside-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(outside);
        File.Copy(Path.Combine(staging, FlavourStore.MarkerFileName), Path.Combine(outside, FlavourStore.MarkerFileName));
        File.WriteAllText(Path.Combine(outside, "data.bin"), "keep");
        var nested = Path.Combine(_store.FlavourFolder(Id), "a", "b");
        Directory.CreateDirectory(nested);
        File.Copy(Path.Combine(staging, FlavourStore.MarkerFileName), Path.Combine(nested, FlavourStore.MarkerFileName));

        Assert.Throws<FlavourSafetyException>(() => _store.DeleteOwnedFolder(outside, Id));
        Assert.Throws<FlavourSafetyException>(() => _store.DeleteOwnedFolder(Path.Combine(_store.FlavourFolder(Id), "..", "..", Path.GetFileName(outside)), Id));
        Assert.Throws<FlavourSafetyException>(() => _store.DeleteOwnedFolder(nested, Id));
        Assert.Throws<FlavourSafetyException>(() => _store.DeleteOwnedFolder(staging, "sd-cuda12"));
        Assert.True(File.Exists(Path.Combine(outside, "data.bin")));
        Assert.True(Directory.Exists(nested));
        Assert.True(Directory.Exists(staging));
    }

    [Fact]
    public void LinkInsideAnOwnedFolderStopsTheDelete()
    {
        var e = Entry(Zip(DefaultFiles));
        var staging = _store.CreateStaging(e);
        var target = Path.Combine(_dir, "link-target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "precious.txt"), "keep");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(staging, "link"), target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // symbolic links need developer mode or elevation
        }

        Assert.Throws<FlavourSafetyException>(() => _store.DeleteOwnedFolder(staging, Id));
        Assert.True(File.Exists(Path.Combine(target, "precious.txt")));
        Assert.True(Directory.Exists(staging));
        Directory.Delete(Path.Combine(staging, "link")); // removes the link only
    }

    [Fact]
    public async Task ReplacingAFlavourRemovesTheOldMarkedVersionOnlyWhenNotInUse()
    {
        var zip = Zip(DefaultFiles);
        var v1 = Entry(zip, tag: "b100");
        var v2 = Entry(zip, tag: "b200");
        var d = Downloader(new ZipServer(zip));
        var old = await d.InstallAsync(v1, null, CancellationToken.None);
        var current = await d.InstallAsync(v2, null, CancellationToken.None);
        Assert.NotEqual(old, current);

        Assert.Empty(_store.RemoveOldVersions(v2, p => p == old));
        Assert.True(Directory.Exists(old));
        Assert.Equal(new[] { old }, _store.RemoveOldVersions(v2, _ => false));
        Assert.False(Directory.Exists(old));
        Assert.True(_store.Verify(current, v2).Ok);
    }

    [Fact]
    public async Task PlantedExtraFileFailsVerification()
    {
        var zip = Zip(DefaultFiles);
        var e = Entry(zip);
        var folder = await Downloader(new ZipServer(zip)).InstallAsync(e, null, CancellationToken.None);
        File.WriteAllText(Path.Combine(folder, "version.dll"), "planted");

        Assert.False(_store.Verify(folder, e).Ok);
        Assert.Null(_store.TryGetVerified(e));
    }

    [Fact]
    public async Task PlaceholderPinsAreRefused()
    {
        var pins = FlavourManifest.Parse(File.ReadAllBytes(Path.Combine(RepoRoot(), "engine", "cuda.lock.json")));
        Assert.Equal(new[] { "cudart12", "llama-cuda12", "sd-cuda12" }, pins.Select(p => p.Id).Order());
        Assert.All(pins, p => Assert.Equal("placeholder", FlavourManifest.Problem(p)));
        Assert.Equal(new[] { "cudart12", "llama-cuda12" }, FlavourManifest.InstallOrder(pins, "llama-cuda12").Select(p => p.Id));

        var server = new ZipServer([]);
        foreach (var p in pins)
        {
            var ex = await Assert.ThrowsAsync<FlavourInstallException>(() => Downloader(server).InstallAsync(p, null, CancellationToken.None));
            Assert.Equal("placeholder", ex.Code);
        }

        Assert.Empty(server.Hosts);
        Assert.False(Directory.Exists(_store.Root));
    }

    [Fact]
    public void ManifestChecksRefuseUnsafeEntries()
    {
        var good = Entry(Zip(DefaultFiles));
        Assert.Null(FlavourManifest.Problem(good));
        Assert.Equal("placeholder", FlavourManifest.Problem(new FlavourEntry { Id = Id, Placeholder = true }));
        Assert.NotNull(FlavourManifest.Problem(Entry(Zip(DefaultFiles), urls: ["http://upstream.test/a.zip"])));
        Assert.NotNull(FlavourManifest.Problem(Entry(Zip(DefaultFiles), files: new Dictionary<string, byte[]> { ["../x.dll"] = [1] })));
        Assert.NotNull(FlavourManifest.Problem(Entry(Zip(DefaultFiles), files: new Dictionary<string, byte[]> { ["C:/x.dll"] = [1] })));
        Assert.NotNull(FlavourManifest.Problem(Entry(Zip(DefaultFiles), files: new Dictionary<string, byte[]> { ["x.dll:ads"] = [1] })));
        Assert.NotNull(FlavourManifest.Problem(Entry(Zip(DefaultFiles), files: new Dictionary<string, byte[]> { ["con.dll"] = [1] })));
        Assert.Throws<InvalidDataException>(() => FlavourManifest.Parse("{\"schema\":2,\"flavours\":[]}"u8));
    }

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "PortalAIHost.slnx")))
            {
                return d.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class SyncProgress(Action<FlavourProgress> report) : IProgress<FlavourProgress>
    {
        public void Report(FlavourProgress value) => report(value);
    }

    private sealed class BigDisk(long free = long.MaxValue / 4) : IDiskInfo
    {
        public long GetFreeBytes(string path) => free;

        public string GetFileSystem(string path) => "NTFS";
    }

    /// <summary>Serves one zip in-process with HTTP range support; can cut the first response short.</summary>
    private sealed class ZipServer(byte[] body) : HttpMessageHandler
    {
        public int CutAfter { get; set; } = -1;
        public long? AdvertisedLength { get; init; }
        public HashSet<string> Missing { get; } = [];
        public List<long?> RangeStarts { get; } = [];
        public List<string> Hosts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Hosts.Add(request.RequestUri!.Host);
            if (Missing.Contains(request.RequestUri.Host))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            var start = request.Headers.Range?.Ranges.First().From;
            RangeStarts.Add(start);
            var slice = body[(int)(start ?? 0)..];
            Stream stream = new MemoryStream(slice);
            if (CutAfter >= 0)
            {
                stream = new BreakingStream(slice, CutAfter);
                CutAfter = -1;
            }

            var content = new StreamContent(stream);
            content.Headers.ContentLength = AdvertisedLength ?? slice.Length;
            var response = new HttpResponseMessage(start is > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = content };
            if (start is > 0)
            {
                content.Headers.ContentRange = new ContentRangeHeaderValue(start.Value, body.Length - 1, body.Length);
            }

            return Task.FromResult(response);
        }
    }

    private sealed class BreakingStream(byte[] data, int cutAfter) : Stream
    {
        private int _pos;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= cutAfter)
            {
                throw new IOException("connection reset (test)");
            }

            var n = Math.Min(count, Math.Min(cutAfter, data.Length) - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
