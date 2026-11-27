using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Planning;

namespace Portal.AiHost.Engine;

/// <summary>Timing and policy knobs for <see cref="EngineSupervisor"/>. Defaults follow architecture section 1.</summary>
public sealed class EngineSupervisorOptions
{
    /// <summary>Chooses the executable for a spec. The bool is true when CPU is forced (Vulkan marked bad).</summary>
    public required Func<EngineLaunchSpec, bool, EngineBinary> ResolveBinary { get; init; }

    public TimeSpan HealthPollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Load timeout; null means 60 s plus 10 s per GB of model files.</summary>
    public TimeSpan? LoadTimeout { get; init; }

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public int HeartbeatMisses { get; init; } = 3;

    public IReadOnlyList<TimeSpan> RestartBackoff { get; init; } =
        new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30) };

    /// <summary>This many crashes inside <see cref="CrashWindow"/> mean Failed.</summary>
    public int MaxCrashes { get; init; } = 3;

    public TimeSpan CrashWindow { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Unload the model after this long without requests; null disables.</summary>
    public TimeSpan? IdleUnloadAfter { get; init; }

    /// <summary>
    /// Read on every idle check instead of <see cref="IdleUnloadAfter"/> when set, so settings and battery changes
    /// apply without a restart. Returning null means keep loaded.
    /// </summary>
    public Func<TimeSpan?>? IdleUnloadPolicy { get; init; }

    public TimeSpan KillWait { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Supervises one engine child process (architecture section 1): random loopback port and
/// per-launch key, Job Object, health and readiness wait, heartbeat, crash restart with backoff,
/// Vulkan-to-CPU fallback, idle unload, and an HTTP proxy for chat, embeddings and tokenize.
/// </summary>
public sealed class EngineSupervisor : IEngineSupervisor, IEngineProcess
{
    private static readonly string[] AllowedForwardPaths = { "/v1/chat/completions", "/v1/embeddings", "/tokenize", "/apply-template", "/v1/speech" };

    private readonly IEngineAdapter _adapter;
    private readonly EngineSupervisorOptions _o;
    private readonly string _kind;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly List<DateTime> _crashes = new();
    private JobObject? _job;
    private int? _cpuCeiling;
    private System.Diagnostics.ProcessPriorityClass? _priority;
    private EngineState _state = EngineState.Stopped;
    private EngineLaunchSpec? _current;
    private EngineLaunchSpec? _requested;
    private Run? _run;
    private CancellationTokenSource? _restartCts;
    private bool _restarting;
    private bool _forceCpu;
    private int _active;
    private long _lastActivityTicks = Stopwatch.GetTimestamp();
    private int _disposed;

    public EngineSupervisor(string kind, IEngineAdapter adapter, EngineSupervisorOptions options)
    {
        _kind = kind;
        _adapter = adapter;
        _o = options;
        if (_o.IdleUnloadPolicy is { } policy)
        {
            _ = Task.Run(() => IdleLoopAsync(policy, TimeSpan.FromSeconds(2)));
        }
        else if (_o.IdleUnloadAfter is { } idle)
        {
            _ = Task.Run(() => IdleLoopAsync(() => idle, TimeSpan.FromMilliseconds(Math.Clamp(idle.TotalMilliseconds / 4, 20, 5000))));
        }
    }

    public string Kind => _kind;

    public EngineState State
    {
        get { lock (_sync) { return _state; } }
    }

    public EngineLaunchSpec? Current
    {
        get { lock (_sync) { return _current; } }
    }

    public event EventHandler<EngineStateChange>? StateChanged;

    /// <summary>Raised when a crash on Vulkan made the supervisor restart on the CPU build.</summary>
    public event EventHandler<string>? BackendFallback;

    /// <summary>True once Vulkan crashed and the supervisor is pinned to the CPU build.</summary>
    public bool VulkanMarkedBad
    {
        get { lock (_sync) { return _forceCpu; } }
    }

    /// <summary>Filtered, capped tail of the engine's console output (never written to disk).</summary>
    public IReadOnlyList<string> RecentOutput
    {
        get { lock (_sync) { return _run?.Ring.Snapshot() ?? Array.Empty<string>(); } }
    }

    public int ActiveRequests => Volatile.Read(ref _active);

    /// <summary>Process id of the running engine, or null.</summary>
    public int? ProcessId
    {
        get { lock (_sync) { return _run?.Proc.Id; } }
    }

    public System.Diagnostics.ProcessPriorityClass? RequestedPriority
    {
        get { lock (_sync) { return _priority; } }
    }

    public bool SetPriority(System.Diagnostics.ProcessPriorityClass priority)
    {
        Process? proc;
        lock (_sync)
        {
            _priority = priority;
            proc = _run?.Proc;
        }

        return proc is not null && TryApplyPriority(proc, priority);
    }

    public int? RequestedCpuCeiling
    {
        get { lock (_sync) { return _cpuCeiling; } }
    }

    public bool SetCpuCeiling(int percent)
    {
        JobObject? job;
        lock (_sync)
        {
            _cpuCeiling = percent;
            job = _run is null ? null : _job;
        }

        return job is not null && TryApplyCpuCeiling(job, percent);
    }

    private static bool TryApplyCpuCeiling(JobObject job, int percent)
    {
        try
        {
            return job.SetCpuRate(PerformanceModel.CpuRateFor(percent));
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static bool TryApplyPriority(Process proc, System.Diagnostics.ProcessPriorityClass priority)
    {
        try
        {
            proc.PriorityClass = priority;
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    // ---- lifecycle ----------------------------------------------------------------

    public async Task StartAsync(EngineLaunchSpec spec, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        CancelRestart();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(null).ConfigureAwait(false);
            lock (_sync)
            {
                _requested = spec;
                _crashes.Clear();
                _restarting = false;
            }

            await LaunchWithFallbackAsync(spec, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        CancelRestart();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(null).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _disposeCts.Cancel();
        CancelRestart();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync(null).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        _job?.Dispose();
    }

    private void CancelRestart()
    {
        CancellationTokenSource? cts;
        lock (_sync)
        {
            cts = _restartCts;
        }

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task LaunchWithFallbackAsync(EngineLaunchSpec spec, CancellationToken ct)
    {
        try
        {
            Run run;
            try
            {
                run = await LaunchCoreAsync(spec, VulkanMarkedBad, ct).ConfigureAwait(false);
            }
            catch (EngineException) when (!VulkanMarkedBad && IsVulkanAttempt(spec))
            {
                // The Vulkan build died while loading: mark it bad and retry once on the CPU build.
                MarkVulkanBad("Vulkan engine failed to load; using the CPU engine.");
                run = await LaunchCoreAsync(spec, true, ct).ConfigureAwait(false);
            }

            _ = run;
        }
        catch (OperationCanceledException)
        {
            await StopCoreAsync(null).ConfigureAwait(false);
            throw;
        }
        catch (EngineException e)
        {
            await CleanupFailedAsync().ConfigureAwait(false);
            SetState(EngineState.Failed, 0, e.Message);
            throw;
        }
    }

    private bool IsVulkanAttempt(EngineLaunchSpec spec) =>
        _o.ResolveBinary(spec, false).Flavour == RuntimeProbe.Vulkan;

    private void MarkVulkanBad(string message)
    {
        lock (_sync)
        {
            _forceCpu = true;
        }

        BackendFallback?.Invoke(this, message);
    }

    private static EngineLaunchSpec ToCpu(EngineLaunchSpec spec) => spec with
    {
        Placement = spec.Placement with
        {
            Backend = RuntimeProbe.Cpu,
            GpuLayers = 0,
            CpuMoeLayers = null,
            Devices = Array.Empty<PlacementDevice>(),
        },
    };

    private async Task<Run> LaunchCoreAsync(EngineLaunchSpec requested, bool forceCpu, CancellationToken ct, bool keepIdleClock = false)
    {
        var binary = _o.ResolveBinary(requested, forceCpu);
        var spec = binary.Flavour == RuntimeProbe.Cpu ? ToCpu(requested) : requested;
        if (!File.Exists(binary.ExePath))
        {
            throw new EngineException(ErrorCodes.EngineError, "Engine executable not found.");
        }

        var endpoint = new EngineEndpoint(GetFreeLoopbackPort(), Convert.ToHexString(RandomNumberGenerator.GetBytes(24)));
        var args = _adapter.BuildArgs(spec, endpoint);
        LlamaServerArgs.AssertLoopbackOnly(args);

        var psi = new ProcessStartInfo(binary.ExePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(binary.ExePath)!,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        SetState(EngineState.Starting, 0, null, spec);
        var ring = new OutputRing();
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => ring.Add(e.Data);
        proc.ErrorDataReceived += (_, e) => ring.Add(e.Data);
        try
        {
            if (!proc.Start())
            {
                throw new EngineException(ErrorCodes.EngineError, "Engine did not start.");
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            proc.Dispose();
            throw new EngineException(ErrorCodes.EngineError, "Engine did not start: " + e.Message, e);
        }

        try
        {
            _job ??= new JobObject();
            _job.SetMemoryLimit(spec.MemoryLimitBytes);
            int? wantedCeiling;
            lock (_sync)
            {
                wantedCeiling = _cpuCeiling;
            }

            _job.SetCpuRate(wantedCeiling is { } c ? PerformanceModel.CpuRateFor(c) : null);
            _job.Assign(proc);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            KillQuietly(proc);
            proc.Dispose();
            throw new EngineException(ErrorCodes.EngineError, "Could not put the engine in a job object: " + e.Message, e);
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            BaseAddress = endpoint.BaseAddress,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
        var run = new Run(proc, http, ring, binary, spec, requested);
        var wantedPriority = RequestedPriority;
        if (wantedPriority is { } wanted)
        {
            TryApplyPriority(proc, wanted);
        }

        lock (_sync)
        {
            _run = run;
            _current = spec;
        }

        SetState(EngineState.Loading, 0, null, spec);
        try
        {
            await WaitReadyAsync(run, ComputeLoadTimeout(spec), ct).ConfigureAwait(false);
        }
        catch
        {
            await TearDownRunAsync(run).ConfigureAwait(false);
            throw;
        }

        // A crash restart is not use: keep the idle clock where the last request left it, so an engine that
        // crashes (or is killed) while idle still unloads on time instead of starting a fresh idle period.
        if (!keepIdleClock)
        {
            lock (_sync)
            {
                _lastActivityTicks = Stopwatch.GetTimestamp();
            }
        }

        SetState(EngineState.Ready, 1, null, spec);
        _ = Task.Run(() => WatchAsync(run));
        _ = Task.Run(() => HeartbeatAsync(run));
        return run;
    }

    private TimeSpan ComputeLoadTimeout(EngineLaunchSpec spec)
    {
        if (_o.LoadTimeout is { } t)
        {
            return t;
        }

        long bytes = 0;
        foreach (var f in spec.ModelFiles)
        {
            try
            {
                bytes += new FileInfo(f).Length;
            }
            catch (IOException)
            {
            }
        }

        return TimeSpan.FromSeconds(60 + 10.0 * bytes / (1024.0 * 1024 * 1024));
    }

    private async Task WaitReadyAsync(Run run, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (run.Proc.HasExited)
            {
                throw new EngineException(ClassifyExit(run), $"Engine exited during load (code {run.Proc.ExitCode}).");
            }

            if (sw.Elapsed > timeout)
            {
                throw new EngineException(ErrorCodes.EngineError, "Engine did not become ready in time.");
            }

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(2));
                using var resp = await run.Http.GetAsync(_adapter.HealthPath, cts.Token).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }

            await Task.Delay(_o.HealthPollInterval, ct).ConfigureAwait(false);
        }
    }

    private static string ClassifyExit(Run run)
    {
        foreach (var line in run.Ring.Snapshot())
        {
            if (line.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
                || line.Contains("failed to allocate", StringComparison.OrdinalIgnoreCase)
                || line.Contains("ErrorOutOfDeviceMemory", StringComparison.OrdinalIgnoreCase)
                || line.Contains("unable to allocate", StringComparison.OrdinalIgnoreCase))
            {
                return ErrorCodes.InsufficientMemory;
            }
        }

        return ErrorCodes.EngineError;
    }

    private static int GetFreeLoopbackPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try
        {
            return ((IPEndPoint)l.LocalEndpoint).Port;
        }
        finally
        {
            l.Stop();
        }
    }

    private static void KillQuietly(Process p)
    {
        try
        {
            if (!p.HasExited)
            {
                p.Kill(true);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private async Task TearDownRunAsync(Run run)
    {
        run.StopRequested = true;
        try
        {
            run.Dead.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        KillQuietly(run.Proc);
        try
        {
            using var cts = new CancellationTokenSource(_o.KillWait);
            await run.Proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        run.Http.Dispose();
        run.Proc.Dispose();
        lock (_sync)
        {
            if (ReferenceEquals(_run, run))
            {
                _run = null;
                _current = null;
            }
        }
    }

    private async Task StopCoreAsync(string? reason, bool alreadyStopping = false)
    {
        Run? run;
        lock (_sync)
        {
            run = _run;
        }

        if (run == null)
        {
            if (State != EngineState.Stopped)
            {
                lock (_sync)
                {
                    _current = null;
                }

                SetState(EngineState.Stopped, 0, reason);
            }

            return;
        }

        if (!alreadyStopping)
        {
            SetState(EngineState.Stopping, 0, reason);
        }

        await TearDownRunAsync(run).ConfigureAwait(false);
        SetState(EngineState.Stopped, 0, reason);
    }

    private async Task CleanupFailedAsync()
    {
        Run? run;
        lock (_sync)
        {
            run = _run;
        }

        if (run != null)
        {
            await TearDownRunAsync(run).ConfigureAwait(false);
        }

        lock (_sync)
        {
            _current = null;
        }
    }

    private void SetState(EngineState state, double progress, string? message, EngineLaunchSpec? spec = null)
    {
        lock (_sync)
        {
            _state = state;
            if (spec != null)
            {
                _current = spec;
            }
        }

        StateChanged?.Invoke(this, new EngineStateChange(_kind, state, progress, message));
    }

    // ---- monitoring ---------------------------------------------------------------

    private async Task WatchAsync(Run run)
    {
        try
        {
            await run.Proc.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        try
        {
            run.Dead.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (run.StopRequested || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        await HandleCrashAsync(run).ConfigureAwait(false);
    }

    private async Task HeartbeatAsync(Run run)
    {
        var misses = 0;
        try
        {
            while (!run.Dead.IsCancellationRequested)
            {
                await Task.Delay(_o.HeartbeatInterval, run.Dead.Token).ConfigureAwait(false);
                bool ok;
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(run.Dead.Token);
                    cts.CancelAfter(_o.HeartbeatTimeout);
                    using var resp = await run.Http.GetAsync(_adapter.HealthPath, cts.Token).ConfigureAwait(false);
                    ok = resp.StatusCode == HttpStatusCode.OK;
                }
                catch (HttpRequestException)
                {
                    ok = false;
                }
                catch (OperationCanceledException) when (!run.Dead.IsCancellationRequested)
                {
                    ok = false;
                }

                misses = ok ? 0 : misses + 1;
                if (misses >= _o.HeartbeatMisses)
                {
                    KillQuietly(run.Proc); // the watcher sees the exit and restarts
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task HandleCrashAsync(Run crashed)
    {
        var cts = new CancellationTokenSource();
        lock (_sync)
        {
            _restartCts = cts;
        }

        var token = cts.Token;
        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            lock (_sync)
            {
                if (!ReferenceEquals(_run, crashed))
                {
                    return;
                }

                _restarting = true;
            }

            var spec = _requested!;
            var wasVulkan = crashed.Binary.Flavour == RuntimeProbe.Vulkan;
            if (wasVulkan)
            {
                MarkVulkanBad("Vulkan engine crashed; restarting on the CPU engine.");
            }

            while (true)
            {
                int count;
                lock (_sync)
                {
                    var now = DateTime.UtcNow;
                    _crashes.Add(now);
                    _crashes.RemoveAll(t => now - t > _o.CrashWindow);
                    count = _crashes.Count;
                }

                if (count >= _o.MaxCrashes)
                {
                    await CleanupFailedAsync().ConfigureAwait(false);
                    SetState(EngineState.Failed, 0, $"Engine crashed {count} times; giving up.");
                    return;
                }

                SetState(EngineState.Loading, 0, "Restarting after a crash.");
                var backoff = _o.RestartBackoff[Math.Min(count - 1, _o.RestartBackoff.Count - 1)];
                try
                {
                    await Task.Delay(backoff, token).ConfigureAwait(false);
                    await TearDownRunAsync(crashed).ConfigureAwait(false);
                    await LaunchCoreAsync(spec, VulkanMarkedBad, token, keepIdleClock: true).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException)
                {
                    return; // Stop or Start took over
                }
                catch (EngineException)
                {
                }
            }
        }
        finally
        {
            lock (_sync)
            {
                _restarting = false;
            }

            _gate.Release();
        }
    }

    private static TimeSpan? ReadPolicy(Func<TimeSpan?> policy)
    {
        try
        {
            return policy();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null; // a broken policy never unloads
        }
    }

    private async Task IdleLoopAsync(Func<TimeSpan?> policy, TimeSpan period)
    {
        try
        {
            while (!_disposeCts.IsCancellationRequested)
            {
                await Task.Delay(period, _disposeCts.Token).ConfigureAwait(false);
                if (ReadPolicy(policy) is not { } idle)
                {
                    continue; // keep loaded
                }

                bool due;
                lock (_sync)
                {
                    due = _state == EngineState.Ready
                        && Volatile.Read(ref _active) == 0
                        && Stopwatch.GetElapsedTime(_lastActivityTicks) >= idle;
                }

                if (!due)
                {
                    continue;
                }

                await _gate.WaitAsync(_disposeCts.Token).ConfigureAwait(false);
                try
                {
                    // Re-read the policy (it may have changed while waiting), then confirm "no requests" and leave
                    // Ready in one step: a request can only start while Ready, so none slips in before the stop.
                    const string reason = "Unloaded after being idle.";
                    due = false;
                    if (ReadPolicy(policy) is { } idleNow)
                    {
                        lock (_sync)
                        {
                            due = _state == EngineState.Ready
                                && Volatile.Read(ref _active) == 0
                                && Stopwatch.GetElapsedTime(_lastActivityTicks) >= idleNow;
                            if (due)
                            {
                                _state = EngineState.Stopping;
                            }
                        }
                    }

                    if (due)
                    {
                        StateChanged?.Invoke(this, new EngineStateChange(_kind, EngineState.Stopping, 0, reason));
                        await StopCoreAsync(reason, alreadyStopping: true).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ---- requests -----------------------------------------------------------------

    private Run Acquire()
    {
        lock (_sync)
        {
            switch (_state)
            {
                case EngineState.Ready:
                    Interlocked.Increment(ref _active);
                    return _run!;
                case EngineState.Starting:
                case EngineState.Loading:
                    throw new EngineException(_restarting ? ErrorCodes.EngineRestarting : ErrorCodes.ModelLoading, "The model is loading.");
                case EngineState.Stopped:
                    throw new EngineException(ErrorCodes.NoModel, "No model is loaded.");
                case EngineState.Failed:
                    throw new EngineException(ErrorCodes.EngineError, "The engine failed.");
                default:
                    throw new EngineException(ErrorCodes.EngineRestarting, "The engine is restarting.");
            }
        }
    }

    private void Release()
    {
        lock (_sync)
        {
            Interlocked.Decrement(ref _active);
            _lastActivityTicks = Stopwatch.GetTimestamp();
        }
    }

    private static Exception Translate(Run run, CancellationToken callerCt, Exception e)
    {
        if (callerCt.IsCancellationRequested && e is OperationCanceledException)
        {
            return e;
        }

        if (run.StopRequested)
        {
            return new EngineException(ErrorCodes.EngineRestarting, "The engine is restarting.", e);
        }

        return new EngineCrashedException("The engine stopped while serving the request.", e);
    }

    private async Task<HttpResponseMessage> SendAsync(Run run, HttpRequestMessage req, HttpCompletionOption option, CancellationToken linked, CancellationToken callerCt)
    {
        try
        {
            var resp = await run.Http.SendAsync(req, option, linked).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var ex = await MapErrorAsync(resp).ConfigureAwait(false);
                resp.Dispose();
                throw ex;
            }

            return resp;
        }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException or IOException)
        {
            throw Translate(run, callerCt, e);
        }
    }

    private static async Task<Exception> MapErrorAsync(HttpResponseMessage resp)
    {
        string body = "";
        try
        {
            body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
        }

        var code = (int)resp.StatusCode;
        if (code == 503)
        {
            return new EngineException(ErrorCodes.ModelLoading, "The model is loading.");
        }

        if (code == 400 && (body.Contains("exceed_context_size", StringComparison.Ordinal)
            || body.Contains("exceeds the available context", StringComparison.OrdinalIgnoreCase)
            || body.Contains("context size", StringComparison.OrdinalIgnoreCase)))
        {
            return new EngineException(ErrorCodes.ContextLengthExceeded, "The prompt is longer than the context window.");
        }

        if (code == 400)
        {
            return new EngineException(ErrorCodes.InvalidRequest, "The engine rejected the request.");
        }

        return new EngineException(ErrorCodes.EngineError, $"The engine answered {code}.");
    }

    private static StringContent JsonBody(JsonNode node) =>
        new(node.ToJsonString(), Encoding.UTF8, "application/json");

    private static JsonObject ChatNode(ChatCompletionRequest request, bool stream)
    {
        var node = JsonSerializer.SerializeToNode(request, AiHostJsonContext.Default.ChatCompletionRequest)!.AsObject();
        node["stream"] = stream;
        if (stream)
        {
            node["stream_options"] = new JsonObject { ["include_usage"] = true };
        }
        else
        {
            node.Remove("stream_options");
        }

        return node;
    }

    public async Task<ChatCompletionResponse> ChatAsync(ChatCompletionRequest request, CancellationToken ct)
    {
        var run = Acquire();
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, run.Dead.Token);
            using var req = new HttpRequestMessage(HttpMethod.Post, _adapter.ChatPath) { Content = JsonBody(ChatNode(request, false)) };
            using var resp = await SendAsync(run, req, HttpCompletionOption.ResponseContentRead, linked.Token, ct).ConfigureAwait(false);
            try
            {
                var text = await resp.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
                return JsonSerializer.Deserialize(text, AiHostJsonContext.Default.ChatCompletionResponse)
                    ?? throw new EngineException(ErrorCodes.EngineError, "Empty engine reply.");
            }
            catch (Exception e) when (e is OperationCanceledException or HttpRequestException or IOException)
            {
                throw Translate(run, ct, e);
            }
        }
        finally
        {
            Release();
        }
    }

    public async IAsyncEnumerable<ChatCompletionChunk> StreamChatAsync(
        ChatCompletionRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var run = Acquire();
        CancellationTokenSource? linked = null;
        HttpRequestMessage? req = null;
        HttpResponseMessage? resp = null;
        StreamReader? reader = null;
        try
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(ct, run.Dead.Token);
            req = new HttpRequestMessage(HttpMethod.Post, _adapter.ChatPath) { Content = JsonBody(ChatNode(request, true)) };
            resp = await SendAsync(run, req, HttpCompletionOption.ResponseHeadersRead, linked.Token, ct).ConfigureAwait(false);
            Stream body;
            try
            {
                body = await resp.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or HttpRequestException or IOException)
            {
                throw Translate(run, ct, e);
            }

            reader = new StreamReader(body, Encoding.UTF8);
            while (true)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(linked.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is OperationCanceledException or HttpRequestException or IOException)
                {
                    throw Translate(run, ct, e);
                }

                if (line == null)
                {
                    if (run.Dead.IsCancellationRequested && !ct.IsCancellationRequested)
                    {
                        throw Translate(run, ct, new IOException("Engine closed the stream."));
                    }

                    yield break;
                }

                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue; // blank separators, SSE comments, event names
                }

                var data = line.AsSpan(5).Trim();
                if (data.SequenceEqual("[DONE]"))
                {
                    yield break;
                }

                var chunk = JsonSerializer.Deserialize(data.ToString(), AiHostJsonContext.Default.ChatCompletionChunk);
                if (chunk != null)
                {
                    yield return chunk;
                }
            }
        }
        finally
        {
            // Disposing the response closes the socket, which is what makes llama-server free the slot.
            reader?.Dispose();
            resp?.Dispose();
            req?.Dispose();
            linked?.Dispose();
            Release();
        }
    }

    public async Task<EmbeddingsResponse> EmbedAsync(EmbeddingsRequest request, CancellationToken ct)
    {
        var run = Acquire();
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, run.Dead.Token);
            var node = JsonSerializer.SerializeToNode(request, AiHostJsonContext.Default.EmbeddingsRequest)!;
            using var req = new HttpRequestMessage(HttpMethod.Post, _adapter.EmbeddingsPath) { Content = JsonBody(node) };
            using var resp = await SendAsync(run, req, HttpCompletionOption.ResponseContentRead, linked.Token, ct).ConfigureAwait(false);
            try
            {
                var text = await resp.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
                return JsonSerializer.Deserialize(text, AiHostJsonContext.Default.EmbeddingsResponse)
                    ?? throw new EngineException(ErrorCodes.EngineError, "Empty engine reply.");
            }
            catch (Exception e) when (e is OperationCanceledException or HttpRequestException or IOException)
            {
                throw Translate(run, ct, e);
            }
        }
        finally
        {
            Release();
        }
    }

    public async Task<int> CountTokensAsync(TokenizeRequest request, CancellationToken ct)
    {
        var run = Acquire();
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, run.Dead.Token);
            string text;
            if (request.Text != null)
            {
                text = request.Text;
            }
            else
            {
                var chat = new ChatCompletionRequest { Messages = request.Messages ?? new List<ChatMessage>() };
                var messages = JsonSerializer.SerializeToNode(chat, AiHostJsonContext.Default.ChatCompletionRequest)!["messages"]!.DeepClone();
                using var tReq = new HttpRequestMessage(HttpMethod.Post, _adapter.ApplyTemplatePath)
                {
                    Content = JsonBody(new JsonObject { ["messages"] = messages }),
                };
                using var tResp = await SendAsync(run, tReq, HttpCompletionOption.ResponseContentRead, linked.Token, ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(await ReadSafeAsync(run, tResp, ct, linked.Token).ConfigureAwait(false));
                text = doc.RootElement.GetProperty("prompt").GetString() ?? "";
            }

            using var req = new HttpRequestMessage(HttpMethod.Post, _adapter.TokenizePath)
            {
                Content = JsonBody(new JsonObject { ["content"] = text, ["parse_special"] = true }),
            };
            using var resp = await SendAsync(run, req, HttpCompletionOption.ResponseContentRead, linked.Token, ct).ConfigureAwait(false);
            using var result = JsonDocument.Parse(await ReadSafeAsync(run, resp, ct, linked.Token).ConfigureAwait(false));
            return result.RootElement.GetProperty("tokens").GetArrayLength();
        }
        finally
        {
            Release();
        }
    }

    private static async Task<string> ReadSafeAsync(Run run, HttpResponseMessage resp, CancellationToken callerCt, CancellationToken linked)
    {
        try
        {
            return await resp.Content.ReadAsStringAsync(linked).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException or IOException)
        {
            throw Translate(run, callerCt, e);
        }
    }

    /// <summary>
    /// Raw pass-through for the front end (SSE stays unbuffered). Only the four proxied routes are
    /// allowed. Dispose the result to end the upstream call and release the busy count.
    /// </summary>
    public async Task<EngineForwardResult> ForwardAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        if (Array.IndexOf(AllowedForwardPaths, path) < 0)
        {
            throw new EngineException(ErrorCodes.NotFound, "Route is not proxied.");
        }

        var run = Acquire();
        CancellationTokenSource? linked = null;
        HttpRequestMessage? req = null;
        try
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(ct, run.Dead.Token);
            req = new HttpRequestMessage(method, path) { Content = content };
            HttpResponseMessage resp;
            try
            {
                resp = await run.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or HttpRequestException or IOException)
            {
                throw Translate(run, ct, e);
            }

            var l = linked;
            var r = req;
            linked = null;
            req = null;
            return new EngineForwardResult(resp, () =>
            {
                r.Dispose();
                l.Dispose();
                Release();
            });
        }
        catch
        {
            req?.Dispose();
            linked?.Dispose();
            Release();
            throw;
        }
    }

    // ---- internals ----------------------------------------------------------------

    private sealed class Run
    {
        public Run(Process proc, HttpClient http, OutputRing ring, EngineBinary binary, EngineLaunchSpec spec, EngineLaunchSpec requested)
        {
            Proc = proc;
            Http = http;
            Ring = ring;
            Binary = binary;
            Spec = spec;
            Requested = requested;
        }

        public Process Proc { get; }
        public HttpClient Http { get; }
        public OutputRing Ring { get; }
        public EngineBinary Binary { get; }
        public EngineLaunchSpec Spec { get; }
        public EngineLaunchSpec Requested { get; }
        public CancellationTokenSource Dead { get; } = new();
        public volatile bool StopRequested;
    }
}

/// <summary>An upstream response being passed through. Dispose ends the call.</summary>
public sealed class EngineForwardResult : IDisposable
{
    private readonly Action _onDispose;
    private int _done;

    internal EngineForwardResult(HttpResponseMessage response, Action onDispose)
    {
        Response = response;
        _onDispose = onDispose;
    }

    public HttpResponseMessage Response { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _done, 1) != 0)
        {
            return;
        }

        Response.Dispose();
        _onDispose();
    }
}
