using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Portal.AiHost.Engine.Upscale;

/// <summary>
/// Adapter for the Best quality add-on: one long-lived Python worker, JSON lines on stdin and stdout.
/// The worker is a child of this process, put in a job object so it cannot outlive the Host.
/// </summary>
public sealed class BestTierUpscaleEngine(BestTierInstall install, IGpuInfoSource hardware) : IUpscaleEngine
{
    private static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _proc;
    private JobObject? _job;

    public string Tier => "best";

    public async Task<UpscaleEngineState> CheckAsync(CancellationToken ct)
    {
        var avail = BestTierGate.Evaluate(hardware);
        if (!avail.IsReady)
        {
            return new UpscaleEngineState(UpscaleEngineStatus.NeedsHardware, avail.Reason);
        }

        var state = install.Check(out var launch);
        if (!state.IsReady || launch is null)
        {
            return state;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var proc = await EnsureStartedAsync(launch, ct).ConfigureAwait(false);
            await SendAsync(proc, "{\"op\":\"probe\"}", ct).ConfigureAwait(false);
            while (await ReadEventAsync(proc, ct).ConfigureAwait(false) is { } ev)
            {
                if (ev.GetProperty("event").GetString() != "probe")
                {
                    continue;
                }

                if (ev.TryGetProperty("ok", out var ok) && ok.GetBoolean())
                {
                    return new UpscaleEngineState(UpscaleEngineStatus.Ready);
                }

                var reason = ev.TryGetProperty("reason", out var r) ? r.GetString() : "unknown";
                return new UpscaleEngineState(UpscaleEngineStatus.Broken, "The add-on cannot use CUDA (" + reason + ").");
            }

            StopWorker();
            return new UpscaleEngineState(UpscaleEngineStatus.Broken, "The add-on stopped without answering.");
        }
        catch (UpscaleEngineException e)
        {
            return new UpscaleEngineState(UpscaleEngineStatus.Broken, e.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpscaleSegmentAsync(UpscaleSegmentRequest request, IProgress<UpscaleProgress>? progress, CancellationToken ct)
    {
        var avail = BestTierGate.Evaluate(hardware);
        if (!avail.IsReady)
        {
            throw new UpscaleEngineException("tier_unavailable", avail.Reason ?? "Best quality is not available.");
        }

        var state = install.Check(out var launch);
        if (!state.IsReady || launch is null)
        {
            throw new UpscaleEngineException("engine_missing", state.Reason ?? "The add-on is not installed.");
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var proc = await EnsureStartedAsync(launch, ct).ConfigureAwait(false);
            await SendAsync(proc, BuildUpscaleCommand(request, install.ModelsDir), ct).ConfigureAwait(false);
            await using var cancelReg = ct.Register(() => RequestCancel(proc));
            while (true)
            {
                JsonElement? ev;
                try
                {
                    ev = await ReadEventAsync(proc, CancellationToken.None).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    throw new UpscaleEngineException("worker_failed", "The add-on did not answer.");
                }

                if (ev is null)
                {
                    StopWorker();
                    ct.ThrowIfCancellationRequested();
                    throw new UpscaleEngineException("worker_failed", "The add-on stopped unexpectedly.");
                }

                var e = ev.Value;
                switch (e.GetProperty("event").GetString())
                {
                    case "progress":
                        progress?.Report(new UpscaleProgress(e.GetProperty("segment").GetInt32(),
                            e.GetProperty("frames_done").GetInt32(), e.GetProperty("frames_total").GetInt32()));
                        break;
                    case "segment_done":
                        return;
                    case "cancelled":
                        throw new OperationCanceledException(ct);
                    case "error":
                        throw new UpscaleEngineException(
                            e.TryGetProperty("code", out var c) ? c.GetString() ?? "worker_failed" : "worker_failed",
                            e.TryGetProperty("message", out var m) ? m.GetString() ?? "The add-on failed." : "The add-on failed.");
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_proc is { HasExited: false } p)
            {
                try
                {
                    await p.StandardInput.WriteLineAsync("{\"op\":\"quit\"}").ConfigureAwait(false);
                    using var cts = new CancellationTokenSource(CancelGrace);
                    await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is OperationCanceledException or IOException or InvalidOperationException)
                {
                }
            }

            StopWorker();
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static string BuildUpscaleCommand(UpscaleSegmentRequest r, string modelsDir)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("op", "upscale");
            w.WriteNumber("segment", r.Segment);
            w.WriteString("input_dir", r.InputDir);
            w.WriteString("output_dir", r.OutputDir);
            w.WriteString("model", r.Model);
            w.WriteString("models_dir", modelsDir);
            w.WriteNumber("scale", r.Scale);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private async Task<Process> EnsureStartedAsync(BestTierLaunch launch, CancellationToken ct)
    {
        if (_proc is { HasExited: false } alive)
        {
            return alive;
        }

        StopWorker();
        var psi = new ProcessStartInfo(launch.Exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = launch.WorkDir,
        };
        foreach (var a in launch.Args)
        {
            psi.ArgumentList.Add(a);
        }

        var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            proc.Dispose();
            throw new UpscaleEngineException("engine_missing", "The add-on did not start: " + e.Message);
        }

        _proc = proc;
        try
        {
            _job = new JobObject();
            _job.Assign(proc);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            StopWorker();
            throw new UpscaleEngineException("engine_missing", "Could not contain the add-on process: " + e.Message);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                while (await proc.StandardError.ReadLineAsync().ConfigureAwait(false) is not null)
                {
                }
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException)
            {
            }
        }, CancellationToken.None);

        // The worker announces itself first.
        try
        {
            var first = await ReadEventAsync(proc, ct, StartTimeout).ConfigureAwait(false);
            if (first is null || first.Value.GetProperty("event").GetString() != "ready")
            {
                StopWorker();
                throw new UpscaleEngineException("engine_missing", "The add-on did not start correctly.");
            }
        }
        catch (TimeoutException)
        {
            StopWorker();
            throw new UpscaleEngineException("engine_missing", "The add-on took too long to start.");
        }

        return proc;
    }

    private static async Task SendAsync(Process proc, string line, CancellationToken ct)
    {
        try
        {
            await proc.StandardInput.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
            await proc.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            throw new UpscaleEngineException("worker_failed", "The add-on stopped unexpectedly.");
        }
    }

    /// <summary>Next JSON event from the worker, or null when it closed its output. Ignores non-JSON lines.</summary>
    private static async Task<JsonElement?> ReadEventAsync(Process proc, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t)
        {
            cts.CancelAfter(t);
        }

        while (true)
        {
            string? line;
            try
            {
                line = await proc.StandardOutput.ReadLineAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException();
            }

            if (line is null)
            {
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("event", out _))
                {
                    return doc.RootElement.Clone();
                }
            }
            catch (JsonException)
            {
            }
        }
    }

    /// <summary>Asks the worker to stop; if it has not answered within the grace period, kills it (our own child only).</summary>
    private static void RequestCancel(Process proc)
    {
        try
        {
            proc.StandardInput.WriteLine("{\"op\":\"cancel\"}");
            proc.StandardInput.Flush();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException)
        {
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(CancelGrace);
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
            catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
            {
            }
        });
    }

    /// <summary>Stops only the worker this adapter started.</summary>
    private void StopWorker()
    {
        var p = _proc;
        _proc = null;
        if (p is not null)
        {
            try
            {
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                }
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            p.Dispose();
        }

        _job?.Dispose();
        _job = null;
    }
}
