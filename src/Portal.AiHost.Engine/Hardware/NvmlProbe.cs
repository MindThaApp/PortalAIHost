using System.Globalization;
using System.Runtime.InteropServices;

namespace Portal.AiHost.Engine.Hardware;

/// <summary>One NVIDIA adapter as NVML reports it.</summary>
public sealed record NvmlDevice(string Name, long TotalBytes, int ComputeMajor, int ComputeMinor)
{
    /// <summary>True when the compute capability is at least <paramref name="major"/>.<paramref name="minor"/>.</summary>
    public bool AtLeast(int major, int minor) => ComputeMajor > major || (ComputeMajor == major && ComputeMinor >= minor);
}

/// <summary>What NVML knows. <see cref="Present"/> is false on a PC without an NVIDIA driver.</summary>
public sealed record NvmlResult(
    bool Present,
    string? DriverVersion,
    int CudaDriverVersion,
    IReadOnlyList<NvmlDevice> Devices,
    string? Error)
{
    /// <summary>The clean "no NVIDIA" answer.</summary>
    public static NvmlResult None(string? reason = null) => new(false, null, 0, [], reason);

    /// <summary>Major part of the driver string ("576.52" gives 576), or 0 when unknown.</summary>
    public int DriverMajor => ParseMajor(DriverVersion);

    /// <summary>CUDA driver version as "12.8" (NVML packs it as 12080), or null when unknown.</summary>
    public string? CudaVersionText => CudaDriverVersion <= 0
        ? null
        : string.Create(CultureInfo.InvariantCulture, $"{CudaDriverVersion / 1000}.{CudaDriverVersion % 1000 / 10}");

    /// <summary>The adapter with the most memory, or null.</summary>
    public NvmlDevice? Best => Devices.Count == 0 ? null : Devices.MaxBy(d => d.TotalBytes);

    public static int ParseMajor(string? driver)
    {
        if (string.IsNullOrWhiteSpace(driver))
        {
            return 0;
        }

        var dot = driver.IndexOf('.');
        var head = dot < 0 ? driver : driver[..dot];
        return int.TryParse(head, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}

/// <summary>Source of NVML facts. The real one loads nvml.dll; tests pass a stub.</summary>
public interface INvmlApi
{
    NvmlResult Query();
}

/// <summary>Runs an <see cref="INvmlApi"/> and turns every failure into "no NVIDIA". Never throws.</summary>
public static class NvmlProbe
{
    public static NvmlResult Probe(INvmlApi? api = null)
    {
        try
        {
            return (api ?? new NvmlNative()).Query();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return NvmlResult.None(e.Message);
        }
    }
}

/// <summary>
/// nvml.dll loaded at run time (it ships with the NVIDIA driver). A missing library, a missing export or a failing
/// call all give <see cref="NvmlResult.None"/>. Read-only: nothing is written anywhere.
/// </summary>
public sealed unsafe class NvmlNative : INvmlApi
{
    private const int Success = 0;

    public NvmlResult Query()
    {
        if (!OperatingSystem.IsWindows())
        {
            return NvmlResult.None("Not Windows.");
        }

        if (!TryLoad(out var lib))
        {
            return NvmlResult.None("NVIDIA driver library not found.");
        }

        var shutdown = (delegate* unmanaged[Cdecl]<int>)Export(lib, "nvmlShutdown");
        var inited = false;
        try
        {
            var init = (delegate* unmanaged[Cdecl]<int>)Export(lib, "nvmlInit_v2");
            var getDriver = (delegate* unmanaged[Cdecl]<byte*, uint, int>)Export(lib, "nvmlSystemGetDriverVersion");
            var getCuda = (delegate* unmanaged[Cdecl]<int*, int>)Export(lib, "nvmlSystemGetCudaDriverVersion_v2");
            if (getCuda == null)
            {
                getCuda = (delegate* unmanaged[Cdecl]<int*, int>)Export(lib, "nvmlSystemGetCudaDriverVersion");
            }

            var getCount = (delegate* unmanaged[Cdecl]<uint*, int>)Export(lib, "nvmlDeviceGetCount_v2");
            var getHandle = (delegate* unmanaged[Cdecl]<uint, IntPtr*, int>)Export(lib, "nvmlDeviceGetHandleByIndex_v2");
            var getName = (delegate* unmanaged[Cdecl]<IntPtr, byte*, uint, int>)Export(lib, "nvmlDeviceGetName");
            var getCc = (delegate* unmanaged[Cdecl]<IntPtr, int*, int*, int>)Export(lib, "nvmlDeviceGetCudaComputeCapability");
            var getMem = (delegate* unmanaged[Cdecl]<IntPtr, ulong*, int>)Export(lib, "nvmlDeviceGetMemoryInfo");

            if (init == null || shutdown == null || getDriver == null || getCount == null || getHandle == null)
            {
                return NvmlResult.None("NVIDIA driver library is incomplete.");
            }

            if (init() != Success)
            {
                return NvmlResult.None("NVML did not start.");
            }

            inited = true;
            var buf = stackalloc byte[96];
            string? driver = null;
            if (getDriver(buf, 96) == Success)
            {
                driver = Marshal.PtrToStringAnsi((IntPtr)buf);
            }

            var cuda = 0;
            if (getCuda != null && getCuda(&cuda) != Success)
            {
                cuda = 0;
            }

            uint count = 0;
            if (getCount(&count) != Success)
            {
                count = 0;
            }

            var mem = stackalloc ulong[3];
            var devices = new List<NvmlDevice>();
            for (uint i = 0; i < count; i++)
            {
                IntPtr handle;
                if (getHandle(i, &handle) != Success)
                {
                    continue;
                }

                var name = "NVIDIA GPU";
                if (getName != null && getName(handle, buf, 96) == Success)
                {
                    name = Marshal.PtrToStringAnsi((IntPtr)buf) ?? name;
                }

                int major = 0, minor = 0;
                if (getCc != null && getCc(handle, &major, &minor) != Success)
                {
                    major = minor = 0;
                }

                // nvmlMemory_t: total, free, used (all unsigned 64-bit).
                long total = 0;
                if (getMem != null && getMem(handle, mem) == Success)
                {
                    total = (long)Math.Min(mem[0], long.MaxValue);
                }

                devices.Add(new NvmlDevice(name, total, major, minor));
            }

            return new NvmlResult(devices.Count > 0, driver, cuda, devices, devices.Count > 0 ? null : "No NVIDIA adapter.");
        }
        finally
        {
            if (inited && shutdown != null)
            {
                shutdown();
            }

            NativeLibrary.Free(lib);
        }
    }

    private static bool TryLoad(out IntPtr lib)
    {
        // The driver puts nvml.dll in System32; older installs keep it under NVSMI.
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvml.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll"),
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c) && NativeLibrary.TryLoad(c, out lib))
            {
                return true;
            }
        }

        lib = IntPtr.Zero;
        return false;
    }

    private static IntPtr Export(IntPtr lib, string name) =>
        NativeLibrary.TryGetExport(lib, name, out var p) ? p : IntPtr.Zero;
}
