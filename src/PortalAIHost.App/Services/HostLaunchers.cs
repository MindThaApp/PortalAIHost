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
/// is already running: when the pipe answers first, nothing is started.
/// </summary>
public sealed class ProcessHostLauncher : IHostLauncher
{
    public const string HostExeName = "PortalAIHost.Service.exe";

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

    public ProcessHostLauncher(Func<CancellationToken, Task<bool>> isHostUp, string? baseDirectory = null,
        Func<string, IStartedProcess?>? start = null, Func<Task<bool>>? protocolStart = null, TimeSpan? wait = null, TimeSpan? poll = null)
    {
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
        FailureKey = null;
        if (await _isHostUp(ct).ConfigureAwait(false))
        {
            return true;
        }

        var exe = Path.Combine(_baseDirectory, HostExeName);
        IStartedProcess? process = null;
        if (File.Exists(exe))
        {
            process = _start(exe);
            if (process is null)
            {
                FailureKey = "Vm_StartFailed";
                return false;
            }
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
            var p = Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            });
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
