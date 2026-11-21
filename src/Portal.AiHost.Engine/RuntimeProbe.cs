using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Portal.AiHost.Engine;

public enum BackendPreference
{
    Auto,
    Gpu,
    Cpu,
}

/// <summary>Result of <c>llama-server --list-devices</c> on the Vulkan build.</summary>
public sealed record VulkanProbeResult(bool Ok, string? DeviceName, long TotalMiB, long FreeMiB, string? Error);

/// <summary>Picks the Vulkan or CPU engine build. The CPU build is always the fallback.</summary>
public static partial class RuntimeProbe
{
    public const string Vulkan = "vulkan";
    public const string Cpu = "cpu";
    public const string ExeName = "llama-server.exe";

    [GeneratedRegex(@"^\s*Vulkan\d+:\s*(?<name>.+?)\s*\((?<total>\d+)\s*MiB(?:,\s*(?<free>\d+)\s*MiB free)?\)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex DeviceLine();

    /// <summary>Runs the probe in its own process so a bad driver only kills the probe.</summary>
    public static async Task<VulkanProbeResult> ProbeVulkanAsync(string exePath, TimeSpan timeout, CancellationToken ct)
    {
        if (!File.Exists(exePath))
        {
            return new VulkanProbeResult(false, null, 0, 0, "Vulkan engine not found.");
        }

        var psi = new ProcessStartInfo(exePath, "--list-devices")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
        };

        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
            if (proc == null)
            {
                return new VulkanProbeResult(false, null, 0, 0, "Probe did not start.");
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var outTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var errTask = proc.StandardError.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            var text = await outTask.ConfigureAwait(false) + "\n" + await errTask.ConfigureAwait(false);
            return ParseListDevices(text, proc.ExitCode);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new VulkanProbeResult(false, null, 0, 0, "Probe timed out.");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new VulkanProbeResult(false, null, 0, 0, e.Message);
        }
        finally
        {
            if (proc != null)
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.Kill(true);
                    }
                }
                catch (InvalidOperationException)
                {
                }

                proc.Dispose();
            }
        }
    }

    public static VulkanProbeResult ParseListDevices(string output, int exitCode)
    {
        if (exitCode != 0)
        {
            return new VulkanProbeResult(false, null, 0, 0, $"Probe exited with code {exitCode}.");
        }

        string? bestName = null;
        long bestTotal = 0;
        long bestFree = 0;
        foreach (Match m in DeviceLine().Matches(output))
        {
            var total = long.Parse(m.Groups["total"].Value, CultureInfo.InvariantCulture);
            if (total > bestTotal)
            {
                bestTotal = total;
                bestName = m.Groups["name"].Value;
                bestFree = m.Groups["free"].Success
                    ? long.Parse(m.Groups["free"].Value, CultureInfo.InvariantCulture)
                    : total;
            }
        }

        return bestTotal > 0
            ? new VulkanProbeResult(true, bestName, bestTotal, bestFree, null)
            : new VulkanProbeResult(false, null, 0, 0, "No Vulkan device with memory reported.");
    }

    /// <summary>
    /// Chooses the build. <paramref name="vulkanProbe"/> is null when no probe ran (a cached
    /// result may be passed instead). A Vulkan build marked bad, a failed probe, or a missing
    /// folder all mean CPU.
    /// </summary>
    public static EngineBinary Select(
        string engineRoot,
        BackendPreference preference,
        VulkanProbeResult? vulkanProbe,
        bool vulkanMarkedBad,
        Architecture? architecture = null)
    {
        var arch = architecture ?? RuntimeInformation.ProcessArchitecture;
        var cpuFolder = arch == Architecture.Arm64 && Directory.Exists(Path.Combine(engineRoot, "cpu-arm64")) ? "cpu-arm64" : Cpu;
        var cpu = new EngineBinary(Cpu, Path.Combine(engineRoot, cpuFolder, ExeName));
        if (preference == BackendPreference.Cpu || arch != Architecture.X64 || vulkanMarkedBad)
        {
            return cpu;
        }

        var vulkanExe = Path.Combine(engineRoot, Vulkan, ExeName);
        if (!File.Exists(vulkanExe))
        {
            return cpu;
        }

        if (vulkanProbe is { Ok: true, TotalMiB: > 0 })
        {
            return new EngineBinary(Vulkan, vulkanExe);
        }

        return cpu;
    }
}
