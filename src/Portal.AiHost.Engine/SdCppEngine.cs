using System.Diagnostics;
using System.Text;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;

namespace Portal.AiHost.Engine;

public sealed class SdCppEngineOptions
{
    /// <summary>Folder with one subfolder per build flavour (vulkan, cpu), each holding the sd.cpp executable.</summary>
    public string EngineRoot { get; init; } = "";

    /// <summary>Executable names tried in each flavour folder (newer releases ship sd-cli.exe, older sd.exe).</summary>
    public IReadOnlyList<string> ExeNames { get; init; } = ["sd-cli.exe", "sd.exe"];

    /// <summary>Overrides the lookup: flavour to executable path, or null when absent (tests run a stand-in).</summary>
    public Func<string, string?>? ResolveExe { get; init; }

    public SdCppOptions Args { get; init; } = new();

    /// <summary>Host log sink. Receives exit codes and error kinds only, never prompt text.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>How long a cancelled run may take to end after the kill.</summary>
    public TimeSpan KillWait { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Generation runs for minutes; below normal keeps the PC responsive for the person using it.</summary>
    public ProcessPriorityClass Priority { get; init; } = ProcessPriorityClass.BelowNormal;
}

/// <summary>
/// The stable-diffusion.cpp generation engine. One child process per job, not a resident server:
/// <list type="bullet">
/// <item>A job runs for seconds to hours, so loading the weights again per job costs little (a few seconds from disk).</item>
/// <item>Ending the process returns every byte of graphics memory and RAM at once, so a chat model can use the card
/// between jobs; a resident server would hold 3-12 GB while idle.</item>
/// <item>Cancel is a kill of the job's own Job object: no half-cancelled state inside a long-lived server, and a crash
/// or driver fault takes down only that one job.</item>
/// <item>The runtime's server mode is newer, its HTTP API is not stable between releases, and it reports neither step
/// progress nor cancellation that this host could rely on.</item>
/// </list>
/// "Loaded" for this engine therefore means: binary present, model files verified, memory plan reserved.
/// </summary>
public sealed class SdCppEngine : IGenerationEngine
{
    public const string Vulkan = RuntimeProbe.Vulkan;
    public const string Cpu = RuntimeProbe.Cpu;

    private readonly SdCppEngineOptions _o;

    public SdCppEngine(SdCppEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _o = options;
    }

    /// <summary>The executable of a flavour, or null when it is not installed.</summary>
    public string? ExeFor(string flavour)
    {
        if (_o.ResolveExe is { } resolve)
        {
            return resolve(flavour) is { } p && File.Exists(p) ? p : null;
        }

        if (string.IsNullOrEmpty(_o.EngineRoot))
        {
            return null;
        }

        foreach (var name in _o.ExeNames)
        {
            var path = Path.Combine(_o.EngineRoot, flavour, name);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    public GenerationEngineCheck Check()
    {
        var gpu = ExeFor(Vulkan) is not null;
        var cpu = ExeFor(Cpu) is not null;
        return gpu || cpu
            ? new GenerationEngineCheck(true, gpu, null)
            : new GenerationEngineCheck(false, false, "The image and video engine is not installed.");
    }

    public bool SupportsFamily(string family, string kind) => SdCppArgs.Supports(family, kind);

    public async Task RunAsync(GenerationLaunch launch, IProgress<GenerationProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ct.ThrowIfCancellationRequested();
        var cpu = string.Equals(launch.Placement.Backend, Cpu, StringComparison.OrdinalIgnoreCase);
        var exe = ExeFor(cpu ? Cpu : Vulkan)
            ?? throw new EngineException(ErrorCodes.EngineMissing,
                cpu ? "The processor build of the image and video engine is not installed." : "The image and video engine is not installed.");

        // Never overwrite: the output path is a fresh name in the job's own work folder.
        if (File.Exists(launch.OutputPath) || Directory.Exists(launch.OutputPath))
        {
            throw new InvalidOperationException("The output path already exists.");
        }

        foreach (var c in launch.Components)
        {
            if (!File.Exists(c.Path))
            {
                throw new EngineException(ErrorCodes.ModelNotInstalled, "A file of the model is missing.");
            }
        }

        var args = SdCppArgs.Build(launch, _o.Args);
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            // The job's work folder: the package folder is read-only, and any stray file lands with the job.
            WorkingDirectory = Path.GetDirectoryName(launch.OutputPath)!,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        var parser = new SdCppProgressParser();
        var ring = new OutputRing(64);
        var sync = new object();
        void OnLine(string line)
        {
            GenerationProgress? p;
            lock (sync)
            {
                p = parser.Parse(line);
                if (!line.Contains('|', StringComparison.Ordinal))
                {
                    ring.Add(line);
                }
            }

            if (p is not null)
            {
                progress?.Report(p);
            }
        }

        using var job = new JobObject();
        job.SetMemoryLimit(launch.MemoryLimitBytes);
        using var proc = new Process { StartInfo = psi };
        try
        {
            if (!proc.Start())
            {
                throw new EngineException(ErrorCodes.EngineError, "The image and video engine did not start.");
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new EngineException(ErrorCodes.EngineError, "The image and video engine did not start: " + e.Message, e);
        }

        try
        {
            job.Assign(proc);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            Kill(proc);
            throw new EngineException(ErrorCodes.EngineError, "Could not put the image and video engine in a job object: " + e.Message, e);
        }

        try
        {
            proc.PriorityClass = _o.Priority;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // The process may already have ended; the exit code tells.
        }

        progress?.Report(new GenerationProgress(GenerationStages.Loading, 0, 0, 0));
        var stdout = PumpAsync(proc.StandardOutput, OnLine);
        var stderr = PumpAsync(proc.StandardError, OnLine);
        using (ct.Register(static state => Kill((Process)state!), proc))
        {
            try
            {
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Engine stop: the kill above ends the process tree; the job object is closed below as a second net.
                Kill(proc);
                using var wait = new CancellationTokenSource(_o.KillWait);
                try
                {
                    await proc.WaitForExitAsync(wait.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _o.Log?.Invoke("Generation engine did not end within the kill wait.");
                }

                throw;
            }
        }

        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        var exit = proc.ExitCode;
        var output = new FileInfo(launch.OutputPath);
        if (exit == 0 && output.Exists && output.Length > 0)
        {
            progress?.Report(new GenerationProgress(GenerationStages.Saving, 0, 0, 1));
            return;
        }

        string? lastError;
        lock (sync)
        {
            lastError = parser.LastError;
        }

        _o.Log?.Invoke($"Generation engine ended with exit code {exit}.");
        if (lastError is not null && LooksLikeOutOfMemory(lastError))
        {
            throw new EngineException(ErrorCodes.InsufficientMemory, "The image and video engine ran out of memory.");
        }

        throw new EngineException(ErrorCodes.EngineError, exit == 0
            ? "The image and video engine wrote no output."
            : $"The image and video engine failed (exit code {exit}).");
    }

    internal static bool LooksLikeOutOfMemory(string text)
    {
        var t = text.ToLowerInvariant();
        return t.Contains("out of memory", StringComparison.Ordinal) || t.Contains("outofdevicememory", StringComparison.Ordinal)
            || t.Contains("failed to allocate", StringComparison.Ordinal) || t.Contains("alloc_buffer", StringComparison.Ordinal);
    }

    /// <summary>Reads a stream and hands over each line; carriage returns end a line too (progress bar redraws).</summary>
    private static async Task PumpAsync(StreamReader reader, Action<string> onLine)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    var ch = buffer[i];
                    if (ch is '\r' or '\n')
                    {
                        if (line.Length > 0)
                        {
                            onLine(line.ToString());
                            line.Clear();
                        }
                    }
                    else if (line.Length < 2048)
                    {
                        line.Append(ch);
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // The process was killed mid-read.
        }

        if (line.Length > 0)
        {
            onLine(line.ToString());
        }
    }

    private static void Kill(Process p)
    {
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }
}
