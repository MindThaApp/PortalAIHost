using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Platform.Windows.Power;

namespace Portal.AiHost.Platform.Windows.Hardware;

/// <summary>
/// Windows hardware check: RAM (GlobalMemoryStatusEx), GPUs with VRAM and live budget (DXGI), CPU cores,
/// fixed drives with free space and file system, and battery/AC. Read-only.
/// </summary>
public sealed partial class WindowsHardwareProbe : IHardwareProbe
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    public bool IsOnBattery => PowerStatus.IsOnBattery();

    public Task<HardwareProfile> ProbeAsync(CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var (total, _) = ReadRam();
        var gpus = ReadGpus().Select(a => new GpuProfile(
            a.Name, Dxgi.VendorName(a.VendorId), $"0x{a.Luid:x16}", a.Driver, a.DedicatedVideoMemory, a.BudgetBytes)).ToList();
        var drives = ReadDrives();
        var cores = Environment.ProcessorCount;
        var arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        return new HardwareProfile("windows", arch, cores, total, gpus, drives, Fingerprint(total, cores, arch, gpus));
    }, ct);

    public MemorySnapshot GetMemory()
    {
        var (total, available) = ReadRam();
        var free = ReadGpus().Select(a => Math.Max(0, (a.BudgetBytes > 0 ? a.BudgetBytes : a.DedicatedVideoMemory) - a.CurrentUsageBytes)).ToArray();
        return new MemorySnapshot(total, available, free);
    }

    /// <summary>
    /// Stable across restarts, changes when RAM, the GPU set, VRAM or the driver change. RAM is rounded to whole
    /// GB so small firmware reservations do not flip it.
    /// </summary>
    public static string Fingerprint(long ramBytes, int cores, string arch, IEnumerable<GpuProfile> gpus)
    {
        var sb = new StringBuilder();
        sb.Append(Math.Round(ramBytes / (double)(1L << 30))).Append('|').Append(cores).Append('|').Append(arch);
        foreach (var g in gpus.OrderBy(g => g.Name, StringComparer.Ordinal).ThenBy(g => g.VramBytes))
        {
            sb.Append('|').Append(g.Name).Append(';').Append(g.Vendor).Append(';').Append(g.VramBytes >> 20).Append(';').Append(g.Driver);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    private static (long Total, long Available) ReadRam()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? ((long)status.TotalPhys, (long)status.AvailPhys) : (0, 0);
    }

    // Hardware adapters with real dedicated memory first; the same order is used by ProbeAsync and GetMemory.
    private static List<DxgiAdapter> ReadGpus() =>
        Dxgi.EnumerateHardwareAdapters()
            .Where(a => a.VendorId != 0x1414)
            .GroupBy(a => a.Luid)
            .Select(g => g.First())
            .OrderByDescending(a => a.DedicatedVideoMemory)
            .ThenBy(a => a.Luid)
            .ToList();

    private static List<DriveProfile> ReadDrives()
    {
        var list = new List<DriveProfile>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable)
                {
                    list.Add(new DriveProfile(d.RootDirectory.FullName, d.DriveFormat, d.AvailableFreeSpace));
                }
            }
            catch (IOException)
            {
                // Drive went away while listing.
            }
            catch (UnauthorizedAccessException)
            {
                // Not readable by this user; skip.
            }
        }

        return list;
    }
}
