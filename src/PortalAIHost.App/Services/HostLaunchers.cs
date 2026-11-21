using System.Diagnostics;

namespace PortalAIHost.App.Services;

/// <summary>A host process this window started.</summary>
public interface IStartedProcess : IDisposable
{
    bool HasExited { get; }
    int ExitCode { get; }
}

/// <summary>
/// Starts the real host: PortalAIHost.Service.exe next to this app, or (packaged, exe not found) the
/// portal-ai-host:start protocol. Then waits until the control pipe answers. Never kills or replaces a host that
/// is already running: when the pipe answers first, nothing is started. A test run (test data root set) never
/// uses the protocol, because that would start the installed host on the real data.
/// </summary>
public sealed class ProcessHostLauncher : IHostLauncher
{
    public const string HostExeName = "PortalAIHost.Service.exe";
    public const string HostSubfolder = "host";

    /// <summary>Argument passed to the host: start (or keep) it without asking a running host to show a window.</summary>
    public const string StartArgument = "portal-ai-host:start";

    /// <summary>Same variable as the host's HostPaths: a test data root for the window and its host.</summary>
    public const string TestRootVariable = "PORTALAIHOST_TEST_DATA_ROOT";

    // Host exit codes (PortalAIHost.Service Program / HostPaths).
    public const int ExitBadTestRoot = 3;
    public const int ExitStartFailed = 4;
    public const int ExitPipeHeldByOtherUser = 5;

    private readonly Func<CancellationToken, Task<bool>> _isHostUp;
    private readonly string _baseDirectory;
    private readonly Func<string, IStartedProcess?> _start;
    private readonly Func<Task<bool>>? _protocolStart;
    private readonly TimeSpan _wait;
    private readonly TimeSpan _poll;
    private readonly Func<string, string?> _getEnv;
    private readonly SemaphoreSlim _startGate = new(1, 1);

    public ProcessHostLauncher(Func<CancellationToken, Task<bool>> isHostUp, string? baseDirectory = null,
        Func<string, IStartedProcess?>? start = null, Func<Task<bool>>? protocolStart = null, TimeSpan? wait = null, TimeSpan? poll = null,
        Func<string, string?>? getEnvironment = null)
    {
        _getEnv = getEnvironment ?? Environment.GetEnvironmentVariable;
        _isHostUp = isHostUp;
        _baseDirectory = baseDirectory ?? AppContext.BaseDirectory;
        _start = start ?? StartProcess;
        _protocolStart = protocolStart;
        _wait = wait ?? TimeSpan.FromSeconds(20);
        _poll = poll ?? TimeSpan.FromMilliseconds(250);
    }

    public string? FailureKey { get; private set; }

    public async Task<bool> StartHostAsync(CancellationToken ct = default)
    {
        // One start at a time: FailureKey belongs to the start that set it.
        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await StartCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task<bool> StartCoreAsync(CancellationToken ct)
    {
        FailureKey = null;
        if (await _isHostUp(ct).ConfigureAwait(false))
        {
            return true;
        }

        // Next to the app, or in a host subfolder (a framework-dependent host cannot share the self-contained app's folder).
        var exe = new[] { Path.Combine(_baseDirectory, HostExeName), Path.Combine(_baseDirectory, HostSubfolder, HostExeName) }
            .FirstOrDefault(File.Exists);
        IStartedProcess? process = null;
        if (exe is not null)
        {
            process = _start(exe);
            if (process is null)
            {
                FailureKey = "Vm_StartFailed";
                return false;
            }
        }
        else if (!string.IsNullOrEmpty(_getEnv(TestRootVariable)))
        {
            FailureKey = "Vm_HostMissingTestRun";
            return false;
        }
        else if (_protocolStart is null || !await _protocolStart().ConfigureAwait(false))
        {
            FailureKey = "Vm_HostMissing";
            return false;
        }

        using (process)
        {
            var deadline = DateTime.UtcNow + _wait;
            while (DateTime.UtcNow < deadline)
            {
                if (await _isHostUp(ct).ConfigureAwait(false))
                {
                    return true;
                }

                if (process is { HasExited: true })
                {
                    // Exit 0 means another host already held the pipe and was asked to show itself; give it a last look.
                    if (process.ExitCode == 0 && await _isHostUp(ct).ConfigureAwait(false))
                    {
                        return true;
                    }

                    FailureKey = KeyForExitCode(process.ExitCode);
                    return false;
                }

                await Task.Delay(_poll, ct).ConfigureAwait(false);
            }
        }

        FailureKey = "Vm_StartTimedOut";
        return false;
    }

    /// <summary>Plain-language message key for a host exit code.</summary>
    public static string KeyForExitCode(int code) => code switch
    {
        ExitBadTestRoot => "Vm_HostExitTestRoot",
        ExitStartFailed => "Vm_HostExitStartFailed",
        ExitPipeHeldByOtherUser => "Vm_HostExitOtherUser",
        _ => "Vm_StartFailed",
    };

    private static IStartedProcess? StartProcess(string exe)
    {
        try
        {
            // The child inherits this process's environment, so a test data root set for the window reaches the host.
            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            };
            info.ArgumentList.Add(StartArgument); // never ask an already running host to show its window
            var p = Process.Start(info);
            return p is null ? null : new StartedProcess(p);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private sealed class StartedProcess(Process p) : IStartedProcess
    {
        public bool HasExited => p.HasExited;
        public int ExitCode => p.ExitCode;
        public void Dispose() => p.Dispose();
    }
}

/// <summary>Launcher used with the fake host: either "starts" the fake or reports that it could not.</summary>
public sealed class FakeHostLauncher : IHostLauncher
{
    private readonly FakeHostControl _host;
    private readonly bool _succeeds;

    public FakeHostLauncher(FakeHostControl host, bool succeeds)
    {
        _host = host;
        _succeeds = succeeds;
    }

    public int Calls { get; private set; }

    public string? FailureKey => _succeeds ? null : "Vm_StartFailed";

    public Task<bool> StartHostAsync(CancellationToken ct = default)
    {
        Calls++;
        if (_succeeds) _host.HostStarted();
        return Task.FromResult(_succeeds);
    }
}
