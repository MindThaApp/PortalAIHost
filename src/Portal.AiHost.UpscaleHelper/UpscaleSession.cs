using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Portal.AiHost.UpscaleHelper;

/// <summary>
/// The helper's pipe protocol. Requests are one JSON line each on the input; replies are one JSON line each on the
/// output. A raw request ("upscale-raw") is followed by width x height x 3 bytes of packed RGB on the input; its
/// "frame" reply is followed by the result bytes. Ops: load, upscale (PNG file in, new PNG file out), upscale-raw,
/// cancel, ping, quit. One job runs at a time. The helper only reads the model and input files; it writes new files
/// (never over an existing one) and removes only its own temporary file.
/// </summary>
internal sealed class UpscaleSession
{
    public const int MaxRawBytes = 512 * 1024 * 1024;

    private readonly Stream _in;
    private readonly Stream _out;
    private readonly Func<JsonObject, IModelRunner> _open;
    private readonly object _writeLock = new();
    private readonly object _jobLock = new();
    private IModelRunner? _runner;
    private int _tile = 256;
    private int _overlap = 16;
    private Task? _job;
    private CancellationTokenSource? _jobCts;

    public UpscaleSession(Stream input, Stream output, Func<JsonObject, IModelRunner> openRunner)
    {
        _in = input;
        _out = output;
        _open = openRunner;
    }

    public async Task<int> RunAsync()
    {
        Send(new JsonObject { ["event"] = "ready", ["version"] = 1 });
        try
        {
            while (ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonObject req;
                try
                {
                    req = JsonNode.Parse(line)?.AsObject() ?? throw new FormatException("Not an object.");
                }
                catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or InvalidOperationException)
                {
                    SendError(null, "Bad request: " + e.Message);
                    continue;
                }

                var op = (string?)req["op"];
                var id = (string?)req["id"];
                switch (op)
                {
                    case "ping":
                        Send(new JsonObject { ["event"] = "pong" });
                        break;
                    case "cancel":
                        lock (_jobLock)
                        {
                            _jobCts?.Cancel();
                        }

                        break;
                    case "quit":
                        CancelAndWait();
                        Send(new JsonObject { ["event"] = "bye" });
                        return 0;
                    case "load":
                        StartJob(id, ct => Load(req, id));
                        break;
                    case "upscale":
                        StartJob(id, ct => UpscaleFile(req, id, ct));
                        break;
                    case "upscale-raw":
                        byte[]? raw = null;
                        var w = (int?)req["width"] ?? 0;
                        var h = (int?)req["height"] ?? 0;
                        if (w <= 0 || h <= 0 || (long)w * h * 3 > MaxRawBytes)
                        {
                            SendError(id, "A raw frame needs a valid width and height.");
                            return 2; // The stream position is unknown now.
                        }

                        raw = new byte[w * h * 3];
                        _in.ReadExactly(raw);
                        StartJob(id, ct => UpscaleRaw(req, id, raw, w, h, ct));
                        break;
                    default:
                        SendError(id, "Unknown op.");
                        break;
                }
            }
        }
        catch (EndOfStreamException)
        {
            // The host went away mid-frame.
        }

        CancelAndWait();
        _runner?.Dispose();
        return 0;
    }

    private void StartJob(string? id, Action<CancellationToken> work)
    {
        lock (_jobLock)
        {
            if (_job is { IsCompleted: false })
            {
                SendError(id, "busy");
                return;
            }

            _jobCts = new CancellationTokenSource();
            var ct = _jobCts.Token;
            _job = Task.Run(() =>
            {
                try
                {
                    work(ct);
                }
                catch (OperationCanceledException)
                {
                    Send(new JsonObject { ["event"] = "cancelled", ["id"] = id });
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    SendError(id, e.Message);
                }
            });
        }
    }

    private void CancelAndWait()
    {
        Task? job;
        lock (_jobLock)
        {
            _jobCts?.Cancel();
            job = _job;
        }

        try
        {
            job?.Wait(TimeSpan.FromSeconds(10));
        }
        catch (AggregateException)
        {
            // Reported by the job itself.
        }
    }

    private void Load(JsonObject req, string? id)
    {
        var tile = (int?)req["tile"] ?? 256;
        var overlap = (int?)req["overlap"] ?? 16;
        Tiler.Plan(tile, tile, tile, overlap); // Validates both numbers.
        var next = _open(req);
        var old = _runner;
        _runner = next;
        old?.Dispose();
        _tile = tile;
        _overlap = overlap;
        var msg = new JsonObject
        {
            ["event"] = "loaded",
            ["id"] = id,
            ["scale"] = next.Scale,
            ["device"] = next.Device,
            ["tile"] = tile,
            ["overlap"] = overlap,
        };
        if (next is OnnxRunner { FallbackReason: { } why })
        {
            msg["fallback"] = why;
        }

        AddMemory(msg);
        Send(msg);
    }

    private void UpscaleFile(JsonObject req, string? id, CancellationToken ct)
    {
        var runner = _runner ?? throw new InvalidOperationException("No model is loaded.");
        var input = (string?)req["input"] ?? throw new ArgumentException("input is required.");
        var outDir = (string?)req["outputDir"] ?? throw new ArgumentException("outputDir is required.");
        var scale = (int?)req["scale"] ?? runner.Scale;
        var sw = Stopwatch.StartNew();
        byte[] bytes;
        using (var fs = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (fs.Length > 512L * 1024 * 1024)
            {
                throw new InvalidDataException("The input file is too large.");
            }

            bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
        }

        var (rgb, w, h) = Png.Decode(bytes);
        var result = Upscaler.Run(runner, rgb, w, h, scale, _tile, _overlap, (d, t) => SendProgress(id, d, t), ct, out var ow, out var oh);
        ct.ThrowIfCancellationRequested();
        var stem = (string?)req["name"] ?? Path.GetFileNameWithoutExtension(input);
        var path = WriteNew(outDir, stem, Png.Encode(result, ow, oh));
        var msg = new JsonObject { ["event"] = "done", ["id"] = id, ["output"] = path, ["width"] = ow, ["height"] = oh, ["seconds"] = sw.Elapsed.TotalSeconds };
        AddMemory(msg);
        Send(msg);
    }

    private void UpscaleRaw(JsonObject req, string? id, byte[] raw, int w, int h, CancellationToken ct)
    {
        var runner = _runner ?? throw new InvalidOperationException("No model is loaded.");
        var scale = (int?)req["scale"] ?? runner.Scale;
        var result = Upscaler.Run(runner, raw, w, h, scale, _tile, _overlap, (d, t) => SendProgress(id, d, t), ct, out var ow, out var oh);
        var msg = new JsonObject { ["event"] = "frame", ["id"] = id, ["width"] = ow, ["height"] = oh, ["bytes"] = result.Length };
        AddMemory(msg);
        lock (_writeLock)
        {
            WriteLineUnlocked(msg);
            _out.Write(result);
            _out.Flush();
        }
    }

    /// <summary>Writes <paramref name="data"/> to a new file <c>stem.png</c> in <paramref name="dir"/>, or <c>stem (2).png</c> and so on; never over a file that exists.</summary>
    internal static string WriteNew(string dir, string stem, byte[] data)
    {
        Directory.CreateDirectory(dir);
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            stem = stem.Replace(c, '_');
        }

        if (stem.Length == 0)
        {
            stem = "upscaled";
        }

        var temp = Path.Combine(dir, "." + Guid.NewGuid().ToString("N") + ".upscale-tmp");
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(data);
                fs.Flush(true);
            }

            for (var n = 1; n < 10000; n++)
            {
                var target = Path.Combine(dir, n == 1 ? stem + ".png" : $"{stem} ({n}).png");
                try
                {
                    File.Move(temp, target, overwrite: false);
                    return target;
                }
                catch (IOException) when (File.Exists(target))
                {
                    // Taken: try the next name.
                }
            }

            throw new IOException("No free file name.");
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp); // Only this call's own temporary file.
            }
        }
    }

    private static void AddMemory(JsonObject msg)
    {
        using var p = Process.GetCurrentProcess();
        msg["peakWorkingSetBytes"] = p.PeakWorkingSet64;
        msg["peakCommitBytes"] = p.PeakVirtualMemorySize64;
    }

    private void SendProgress(string? id, int done, int total) =>
        Send(new JsonObject { ["event"] = "progress", ["id"] = id, ["tilesDone"] = done, ["tilesTotal"] = total });

    private void SendError(string? id, string message) =>
        Send(new JsonObject { ["event"] = "error", ["id"] = id, ["message"] = message });

    private void Send(JsonObject o)
    {
        lock (_writeLock)
        {
            WriteLineUnlocked(o);
            _out.Flush();
        }
    }

    private void WriteLineUnlocked(JsonObject o)
    {
        _out.Write(Encoding.UTF8.GetBytes(o.ToJsonString() + "\n"));
    }

    private string? ReadLine()
    {
        var buf = new List<byte>();
        while (true)
        {
            var b = _in.ReadByte();
            if (b < 0)
            {
                return buf.Count == 0 ? null : Encoding.UTF8.GetString([.. buf]);
            }

            if (b == '\n')
            {
                return Encoding.UTF8.GetString([.. buf]).TrimEnd('\r');
            }

            if (buf.Count > 1 << 20)
            {
                throw new InvalidDataException("Request line too long.");
            }

            buf.Add((byte)b);
        }
    }
}
