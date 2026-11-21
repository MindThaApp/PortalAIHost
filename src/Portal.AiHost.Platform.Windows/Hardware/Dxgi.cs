using System.Runtime.InteropServices;

namespace Portal.AiHost.Platform.Windows.Hardware;

/// <summary>One hardware adapter as reported by DXGI.</summary>
internal sealed record DxgiAdapter(
    string Name, uint VendorId, long Luid, long DedicatedVideoMemory, long BudgetBytes, long CurrentUsageBytes, string Driver);

/// <summary>
/// Minimal DXGI enumeration through raw vtable calls (no COM interop runtime, trimming and AOT safe).
/// Read-only: it only lists adapters and queries memory.
/// </summary>
internal static unsafe partial class Dxgi
{
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const uint AdapterFlagSoftware = 2;

    private static readonly Guid IidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IidAdapter3 = new("645967a4-1392-4310-a798-8053ce3e93fd");
    private static readonly Guid IidDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VideoMemoryInfo
    {
        public ulong Budget;
        public ulong CurrentUsage;
        public ulong AvailableForReservation;
        public ulong CurrentReservation;
    }

    [LibraryImport("dxgi.dll")]
    private static partial int CreateDXGIFactory1(Guid* riid, nint* factory);

    public static IReadOnlyList<DxgiAdapter> EnumerateHardwareAdapters()
    {
        var result = new List<DxgiAdapter>();
        nint factory = 0;
        var iid = IidFactory1;
        try
        {
            if (CreateDXGIFactory1(&iid, &factory) < 0 || factory == 0)
            {
                return result;
            }
        }
        catch (DllNotFoundException)
        {
            return result;
        }
        catch (EntryPointNotFoundException)
        {
            return result;
        }

        try
        {
            var enumAdapters1 = (delegate* unmanaged<nint, uint, nint*, int>)Vtbl(factory)[12];
            for (uint i = 0; ; i++)
            {
                nint adapter = 0;
                var hr = enumAdapters1(factory, i, &adapter);
                if (hr == DxgiErrorNotFound || hr < 0 || adapter == 0)
                {
                    break;
                }

                try
                {
                    var a = Describe(adapter);
                    if (a is not null)
                    {
                        result.Add(a);
                    }
                }
                finally
                {
                    Release(adapter);
                }
            }
        }
        finally
        {
            Release(factory);
        }

        return result;
    }

    private static DxgiAdapter? Describe(nint adapter)
    {
        AdapterDesc1 desc;
        var getDesc1 = (delegate* unmanaged<nint, AdapterDesc1*, int>)Vtbl(adapter)[10];
        if (getDesc1(adapter, &desc) < 0 || (desc.Flags & AdapterFlagSoftware) != 0)
        {
            return null;
        }

        var name = new string(desc.Description).TrimEnd('\0').Trim();
        var luid = ((long)desc.LuidHigh << 32) | desc.LuidLow;

        long umd = 0;
        var iidDevice = IidDevice;
        var checkSupport = (delegate* unmanaged<nint, Guid*, long*, int>)Vtbl(adapter)[9];
        var driver = checkSupport(adapter, &iidDevice, &umd) >= 0
            ? $"{(umd >> 48) & 0xFFFF}.{(umd >> 32) & 0xFFFF}.{(umd >> 16) & 0xFFFF}.{umd & 0xFFFF}"
            : "unknown";

        long budget = 0, usage = 0;
        nint adapter3 = 0;
        var iid3 = IidAdapter3;
        var qi = (delegate* unmanaged<nint, Guid*, nint*, int>)Vtbl(adapter)[0];
        if (qi(adapter, &iid3, &adapter3) >= 0 && adapter3 != 0)
        {
            try
            {
                VideoMemoryInfo info;
                var query = (delegate* unmanaged<nint, uint, int, VideoMemoryInfo*, int>)Vtbl(adapter3)[14];
                if (query(adapter3, 0, 0 /* local segment */, &info) >= 0)
                {
                    budget = (long)info.Budget;
                    usage = (long)info.CurrentUsage;
                }
            }
            finally
            {
                Release(adapter3);
            }
        }

        return new DxgiAdapter(name, desc.VendorId, luid, (long)desc.DedicatedVideoMemory, budget, usage, driver);
    }

    private static nint* Vtbl(nint comObject) => *(nint**)comObject;

    private static void Release(nint comObject)
    {
        if (comObject != 0)
        {
            ((delegate* unmanaged<nint, uint>)Vtbl(comObject)[2])(comObject);
        }
    }

    public static string VendorName(uint vendorId) => vendorId switch
    {
        0x1002 or 0x1022 => "amd",
        0x10DE => "nvidia",
        0x8086 => "intel",
        0x5143 or 0x4D4F4351 => "qualcomm",
        0x1414 => "microsoft",
        _ => $"0x{vendorId:x4}",
    };
}
