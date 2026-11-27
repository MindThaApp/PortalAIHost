using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;

namespace Portal.AiHost.Engine;

/// <summary>What the helper reports after loading a model.</summary>
public sealed record UpscaleLoaded(int Scale, string Device, string? Fallback, long PeakWorkingSetBytes);

/// <summary>Result of upscaling a picture file: the new file the helper wrote.</summary>
public sealed record UpscaleFileResult(string OutputPath, int Width, int Height, double Seconds, long PeakWorkingSetBytes);

/// <summary>Result of upscaling a raw RGB frame: packed RGB bytes.</summary>
public sealed record UpscaleFrameResult(byte[] Rgb, int Width, int Height, long PeakWorkingSetBytes);

/// <summary>
/// Client and supervisor for the upscale helper (<c>portal-upscale.exe</c>, ONNX Runtime with DirectML or CPU). It starts
/// the helper in its own Job Object (so it ends with the host), speaks the helper's line protocol over stdin and stdout,
/// answers health checks with a ping, restarts the helper on the next call after it died, and kills only the one child
/// process it started. One request runs at a time. It writes no file itself; the helper writes the new output files.
/// </summary>
public sealed class UpscaleHelper : IAsyncDisposable
{
    private readonly string _exe;
    private readonly TimeSpan _killWait;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private JobObject? _job;
    private Process? _proc;
    private Stream? _stdin;
    private Task? _reader;
    private TaskCompletionSource<JsonObject>? _pending;
    private Action<int, int>? _onProgress;
    private byte[]? _framePayload;
    private JsonObject? _loadRequest;
    private int _disposed;

    public UpscaleHelper(string exePath, TimeSpan? killWait = null)
    {
        _exe = exePath;
        _killWait = killWait ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>True while the helper process is alive.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _proc is { HasExited: false };
            }
        }
    }

    /// <summary>Loads a model. <paramref name="device"/> is "cpu", "dml" or "dml:N" (adapter number); <paramref name="tile"/> and <paramref name="overlap"/> are input pixels.</summary>
    public async Task<UpscaleLoaded> LoadAsync(string modelPath, string device, int tile, int overlap, int threads, CancellationToken ct)
    {
        var req = new JsonObject { ["op"] = "load", ["model"] = modelPath, ["device"] = device, ["tile"] = tile, ["overlap"] = overlap };
        if (threads > 0)
        {
            req["threads"] = threads;
        }

        var reply = await RequestAsync(req, null, null, ct).ConfigureAwait(false);
        _loadRequest = req;
        return new UpscaleLoaded((int)reply["scale"]!, (string)reply["device"]!, (string?)reply["fallback"], (long?)reply["peakWorkingSetBytes"] ?? 0);
    }

    /// <summary>Upscales a PNG file; the helper writes a new file in <paramref name="outputDir"/> and never overwrites one.</summary>
    public async Task<UpscaleFileResult> UpscaleFileAsync(string input, string outputDir, string? name, int scale,
        Action<int, int>? onProgress, CancellationToken ct)
    {
        var req = new JsonObject { ["op"] = "upscale", ["input"] = input, ["outputDir"] = outputDir, ["scale"] = scale };
        if (name is not null)
        {
            req["name"] = name;
        }

        var r = await RequestAsync(req, null, onProgress, ct).ConfigureAwait(false);
        return new UpscaleFileResult((string)r["output"]!, (int)r["width"]!, (int)r["height"]!, (double?)r["seconds"] ?? 0, (long?)r["peakWorkingSetBytes"] ?? 0);
    }

    /// <summary>Upscales one frame of packed RGB bytes (width x height x 3).</summary>
    public async Task<UpscaleFrameResult> UpscaleRawAsync(byte[] rgb, int width, int height, int scale,
        Action<int, int>? onProgress, CancellationToken ct)
    {
        if (rgb.Length != width * height * 3)
        {
            throw new ArgumentException("The buffer does not match the size.", nameof(rgb));
        }

        var req = new JsonObject { ["op"] = "upscale-raw", ["width"] = width, ["height"] = height, ["scale"] = scale };
        var r = await RequestAsync(req, rgb, onProgress, ct).ConfigureAwait(false);
        return new UpscaleFrameResult(_framePayload ?? [], (int)r["width"]!, (int)r["height"]!, (long?)r["peakWorkingSetBytes"] ?? 0);
    }

    /// <summary>Asks the helper to answer; false when it does not within <paramref name="timeout"/> (it is then stopped).</summary>
    public async Task<bool> HealthAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (!IsRunning)
        {
            return false;
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await RequestAsync(new JsonObject { ["op"] = "ping" }, null, null, limit.Token, start: false).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (e is OperationCanceledException or EngineException)
        {
            ct.ThrowIfCancellationRequested();
            Stop();
            return false;
        }
    }

    /// <summary>Ends the helper process this object started (and nothing else).</summary>
    public void Stop()
    {
        Process? p;
        lock (_sync)
        {
            p = _proc;
        }

        KillChild(p);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Stop();
        Task? reader;
        lock (_sync)
        {
            reader = _reader;
        }

        if (reader is not null)
        {
            await Task.WhenAny(reader, Task.Delay(_killWait)).ConfigureAwait(false);
        }

        lock (_sync)
        {
            _proc?.Dispose();
            _proc = null;
            _job?.Dispose();
            _job = null;
        }
    }

    private async Task<JsonObject> RequestAsync(JsonObject req, byte[]? payload, Action<int, int>? onProgress, CancellationToken ct, bool start = true)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (start)
            {
                await EnsureStartedAsync(ct).ConfigureAwait(false);
            }

            Stream stdin;
            TaskCompletionSource<JsonObject> pending;
            lock (_sync)
            {
                stdin = _stdin ?? throw new EngineException(ErrorCodes.EngineError, "The upscale helper is not running.");
                pending = _pending = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
                _onProgress = onProgress;
                _framePayload = null;
            }

            try
            {
                var line = Encoding.UTF8.GetBytes(req.ToJsonString() + "\n");
                await stdin.WriteAsync(line, CancellationToken.None).ConfigureAwait(false);
                if (payload is not null)
                {
                    await stdin.WriteAsync(payload, CancellationToken.None).ConfigureAwait(false);
                }

                await stdin.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (IOException e)
            {
                KillChild(_proc);
                throw new EngineException(ErrorCodes.EngineError, "The upscale helper stopped: " + e.Message, e);
            }

            using var reg = ct.Register(() => CancelRequest(stdin));
            try
            {
                return await pending.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The helper confirmed the cancel, or was ended for it.
                ct.ThrowIfCancellationRequested();
                throw;
            }
        }
        finally
        {
            lock (_sync)
            {
                _pending = null;
                _onProgress = null;
            }

            _gate.Release();
        }
    }

    /// <summary>Asks the helper to stop the running job; if it has not answered after the kill wait, ends the helper.</summary>
    private void CancelRequest(Stream stdin)
    {
        try
        {
            stdin.Write("{\"op\":\"cancel\"}\n"u8);
            stdin.Flush();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // Gone already.
        }

        var p = _proc;
        _ = Task.Delay(_killWait).ContinueWith(_ =>
        {
            TaskCompletionSource<JsonObject>? pending;
            lock (_sync)
            {
                pending = _pending;
            }

            if (pending is { Task.IsCompleted: false })
            {
                KillChild(p);
            }
        }, TaskScheduler.Default);
    }

    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        if (IsRunning)
        {
            return;
        }

        CleanUp();
        var psi = new ProcessStartInfo(_exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(_exe) ?? Environment.CurrentDirectory,
        };
        var job = new JobObject();
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        try
        {
            if (!proc.Start())
            {
                throw new EngineException(ErrorCodes.EngineError, "The upscale helper did not start.");
            }

            job.Assign(proc);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            KillChild(proc);
            proc.Dispose();
            job.Dispose();
            throw new EngineException(ErrorCodes.EngineError, "The upscale helper did not start: " + e.Message, e);
        }

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            _job = job;
            _proc = proc;
            _stdin = proc.StandardInput.BaseStream;
            _reader = Task.Run(() => ReadLoopAsync(proc, ready));
        }

        _ = proc.StandardError.ReadToEndAsync(); // Drained so the helper never blocks on a full pipe.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await ready.Task.WaitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillChild(proc);
            ct.ThrowIfCancellationRequested();
            throw new EngineException(ErrorCodes.EngineError, "The upscale helper did not say it was ready.");
        }

        // A model loaded before a crash is loaded again, so a restart is invisible to the caller.
        if (_loadRequest is { } load)
        {
            // The load reply goes through the same pending slot; a failure here surfaces on the caller's request.
            var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                _pending = tcs;
            }

            await _stdin!.WriteAsync(Encoding.UTF8.GetBytes(load.ToJsonString() + "\n"), CancellationToken.None).ConfigureAwait(false);
            await _stdin.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private void CleanUp()
    {
        lock (_sync)
        {
            _proc?.Dispose();
            _proc = null;
            _stdin = null;
            _job?.Dispose();
            _job = null;
        }
    }

    private async Task ReadLoopAsync(Process proc, TaskCompletionSource ready)
    {
        var stream = proc.StandardOutput.BaseStream;
        var buf = new List<byte>(256);
        var one = new byte[1];
        try
        {
            while (true)
            {
                buf.Clear();
                while (true)
                {
                    var n = await stream.ReadAsync(one).ConfigureAwait(false);
                    if (n == 0)
                    {
                        throw new EndOfStreamException();
                    }

                    if (one[0] == '\n')
                    {
                        break;
                    }

                    buf.Add(one[0]);
                }

                JsonObject msg;
                try
                {
                    msg = JsonNode.Parse(Encoding.UTF8.GetString([.. buf]))!.AsObject();
                }
                catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or NullReferenceException)
                {
                    continue;
                }

                var ev = (string?)msg["event"];
                switch (ev)
                {
                    case "ready":
                        ready.TrySetResult();
                        break;
                    case "progress":
                        Action<int, int>? cb;
                        lock (_sync)
                        {
                            cb = _onProgress;
                        }

                        cb?.Invoke((int?)msg["tilesDone"] ?? 0, (int?)msg["tilesTotal"] ?? 0);
                        break;
                    case "frame":
                        var count = (int)msg["bytes"]!;
                        var payload = new byte[count];
                        await stream.ReadExactlyAsync(payload).ConfigureAwait(false);
                        lock (_sync)
                        {
                            _framePayload = payload;
                        }

                        Complete(tcs => tcs.TrySetResult(msg));
                        break;
                    case "loaded":
                    case "done":
                    case "pong":
                        Complete(tcs => tcs.TrySetResult(msg));
                        break;
                    case "cancelled":
                        Complete(tcs => tcs.TrySetCanceled());
                        break;
                    case "error":
                        var text = (string?)msg["message"] ?? "The upscale helper reported an error.";
                        Complete(tcs => tcs.TrySetException(new EngineException(ErrorCodes.EngineError, text)));
                        break;
                    case "bye":
                        return;
                }
            }
        }
        catch (Exception e) when (e is EndOfStreamException or IOException or ObjectDisposedException)
        {
            ready.TrySetException(new EngineException(ErrorCodes.EngineError, "The upscale helper stopped."));
            Complete(tcs => tcs.TrySetException(new EngineException(ErrorCodes.EngineError, "The upscale helper stopped.")));
        }
    }

    private void Complete(Action<TaskCompletionSource<JsonObject>> act)
    {
        TaskCompletionSource<JsonObject>? tcs;
        lock (_sync)
        {
            tcs = _pending;
        }

        if (tcs is not null)
        {
            act(tcs);
        }
    }

    /// <summary>Ends the one process this class started and its children (never any other process).</summary>
    private static void KillChild(Process? p)
    {
        try
        {
            if (p is { HasExited: false })
            {
                p.Kill(entireProcessTree: true);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }
}
