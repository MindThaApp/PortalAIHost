using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Portal.AiHost.Core.Catalogue;
using Portal.AiHost.Core.Store;

namespace Portal.AiHost.Core.Tests.Models;

/// <summary>
/// A test-only folder under %TEMP%\PortalAIHostTests. Cleanup removes it only when it still carries the
/// test-root marker this class wrote, and removes junctions first without following them.
/// </summary>
internal sealed class TestRoot : IDisposable
{
    private const string Marker = ".portal-ai-host-test-root";

    public TestRoot()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PortalAIHostTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        File.WriteAllText(System.IO.Path.Combine(Path, Marker), "test");
    }

    public string Path { get; }

    public string Sub(params string[] parts)
    {
        var p = System.IO.Path.Combine([Path, .. parts]);
        Directory.CreateDirectory(p);
        return p;
    }

    public static void CreateJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit(10_000);
        if (!Directory.Exists(link))
        {
            throw new InvalidOperationException("Could not create a junction for the test.");
        }
    }

    public void Dispose()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PortalAIHostTests");
        if (!Path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(System.IO.Path.Combine(Path, Marker)))
        {
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(Path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }).ToList())
        {
            var info = new DirectoryInfo(dir);
            if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                info.Delete(); // removes the link itself, never the target
            }
        }

        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; %TEMP% is cleaned eventually.
        }
    }
}

internal sealed class FakeDisk(long freeBytes = long.MaxValue / 4, string fileSystem = "NTFS") : IDiskInfo
{
    public long FreeBytes { get; set; } = freeBytes;
    public string FileSystem { get; set; } = fileSystem;

    public long GetFreeBytes(string path) => FreeBytes;

    public string GetFileSystem(string path) => FileSystem;
}

internal static class TestData
{
    public static byte[] RandomBytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    public static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public const string Commit = "0123456789abcdef0123456789abcdef01234567";

    public static CatalogueEntry Entry(string id, params (string Name, byte[] Data)[] files) => new()
    {
        Id = id,
        DisplayName = id,
        Kind = "chat",
        Engine = "llama-server",
        HfRepo = "test/repo",
        HfCommit = Commit,
        ParamsTotalB = 1,
        ParamsActiveB = 1,
        ContextDefault = 4096,
        ContextMax = 4096,
        Licence = new CatalogueLicence { Spdx = "MIT", Url = "https://example.org/licence", CommercialOk = true },
        Memory = new ModelMemory { WeightsBytes = files.Sum(f => (long)f.Data.Length), NLayers = 4, KvBytesPer1kCtx = 1024 },
        Files = files.Select(f => new CatalogueFile
        {
            Name = f.Name,
            Size = f.Data.Length,
            Sha256 = Sha(f.Data),
            Urls = { $"https://huggingface.co/test/repo/resolve/{Commit}/{f.Name}" },
        }).ToList(),
    };

    public static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "PortalAIHost.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("repo root not found");
    }

    public static byte[] SeedCatalogueBytes() =>
        File.ReadAllBytes(System.IO.Path.Combine(RepoRoot(), "catalogue", "catalogue.json"));
}

/// <summary>Request details the server saw.</summary>
internal sealed record SeenRequest(string Path, string? Range, string? IfRange, string? UserAgent, int Status);

/// <summary>
/// Minimal in-process HTTP/1.1 server on 127.0.0.1 with Range, If-Range (ETag) and fault injection.
/// No real network is used by the download tests.
/// </summary>
internal sealed class RangeServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly object _lock = new();

    public RangeServer()
    {
        _listener.Start();
        BaseUri = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
        _loop = Task.Run(AcceptLoopAsync);
    }

    public Uri BaseUri { get; }
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
    public string Etag { get; set; } = "\"v1\"";
    public bool IgnoreRange { get; set; }

    /// <summary>Per request number (1-based): close the connection after this many body bytes.</summary>
    public Func<int, int?> DropAfter { get; set; } = _ => null;

    /// <summary>Delay per 64 KB chunk, to make cancellation tests deterministic.</summary>
    public TimeSpan ChunkDelay { get; set; }

    public List<SeenRequest> Requests { get; } = new();

    public long BodyBytesSent;

    public HttpClient CreateClient() => new(new RewriteHandler(BaseUri)) { Timeout = TimeSpan.FromSeconds(30) };

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            var header = await ReadHeaderAsync(stream);
            var lines = header.Split("\r\n");
            var path = Uri.UnescapeDataString(lines[0].Split(' ')[1]);
            string? Get(string name) => lines.Skip(1).Select(l => l.Split(':', 2)).Where(p => p.Length == 2
                && p[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase)).Select(p => p[1].Trim()).FirstOrDefault();
            var range = Get("Range");
            var ifRange = Get("If-Range");
            int number;
            if (!Files.TryGetValue(path, out var data))
            {
                lock (_lock)
                {
                    Requests.Add(new SeenRequest(path, range, ifRange, Get("User-Agent"), 404));
                }

                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                return;
            }

            long start = 0;
            var partial = range is not null && !IgnoreRange && (ifRange is null || ifRange == Etag)
                && range.StartsWith("bytes=", StringComparison.Ordinal);
            if (partial)
            {
                start = long.Parse(range!["bytes=".Length..].TrimEnd('-'));
            }

            var status = partial ? 206 : 200;
            lock (_lock)
            {
                Requests.Add(new SeenRequest(path, range, ifRange, Get("User-Agent"), status));
                number = Requests.Count;
            }

            var length = data.Length - start;
            var sb = new StringBuilder();
            sb.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
            sb.Append($"Content-Length: {length}\r\nETag: {Etag}\r\nAccept-Ranges: bytes\r\nConnection: close\r\n");
            if (partial)
            {
                sb.Append($"Content-Range: bytes {start}-{data.Length - 1}/{data.Length}\r\n");
            }

            sb.Append("\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()));
            var limit = DropAfter(number) ?? int.MaxValue;
            var sent = 0L;
            for (var pos = start; pos < data.Length && sent < limit; pos += 65536)
            {
                var n = (int)Math.Min(Math.Min(65536, data.Length - pos), limit - sent);
                await stream.WriteAsync(data.AsMemory((int)pos, n));
                sent += n;
                Interlocked.Add(ref BodyBytesSent, n);
                if (ChunkDelay > TimeSpan.Zero)
                {
                    await Task.Delay(ChunkDelay);
                }
            }

            await stream.FlushAsync(); // closing early (sent < length) gives the client a premature end of body
        }
        catch (IOException)
        {
            // Client went away.
        }
        catch (SocketException)
        {
        }
    }

    private static async Task<string> ReadHeaderAsync(NetworkStream stream)
    {
        var buffer = new List<byte>();
        var one = new byte[1];
        while (await stream.ReadAsync(one) == 1)
        {
            buffer.Add(one[0]);
            if (buffer.Count >= 4 && buffer[^1] == '\n' && buffer[^2] == '\r' && buffer[^3] == '\n' && buffer[^4] == '\r')
            {
                break;
            }
        }

        return Encoding.ASCII.GetString(buffer.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (Exception)
        {
            // Stopping.
        }

        _stop.Dispose();
    }

    /// <summary>Sends https://huggingface.co/... requests to the local server, keeping the path.</summary>
    private sealed class RewriteHandler(Uri target) : DelegatingHandler(new SocketsHttpHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.RequestUri = new Uri(target, request.RequestUri!.PathAndQuery);
            return base.SendAsync(request, ct);
        }
    }
}
