using System.Runtime.InteropServices;
using Portal.AiHost.Core.Download;

namespace Portal.AiHost.Platform.Windows.Power;

/// <summary>Battery and AC state (GetSystemPowerStatus).</summary>
public static partial class PowerStatus
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SystemPowerStatus status);

    /// <summary>True only when the PC reports it is running from its battery (AC line offline).</summary>
    public static bool IsOnBattery() => GetSystemPowerStatus(out var s) && s.AcLineStatus == 0;

    /// <summary>Battery charge 0-100, or null when there is no battery or it is unknown.</summary>
    public static int? BatteryPercent() =>
        GetSystemPowerStatus(out var s) && s.BatteryLifePercent <= 100 && (s.BatteryFlag & 128) == 0 ? s.BatteryLifePercent : null;
}

/// <summary>
/// Keeps the system awake while a download runs, with a handle-based power request
/// (PowerSetRequest SystemRequired), so it works from any thread and ends when disposed.
/// The display may still turn off.
/// </summary>
public sealed partial class PowerRequestSleepBlocker : ISleepBlocker
{
    private const int PowerRequestSystemRequired = 1;
    private const uint PowerRequestContextVersion = 0;
    private const uint PowerRequestContextSimpleString = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public nint SimpleReasonString;
        public nint Reserved1;
        public nint Reserved2;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint PowerCreateRequest(ref ReasonContext context);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PowerSetRequest(nint handle, int type);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PowerClearRequest(nint handle, int type);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    public IDisposable Acquire(string reason)
    {
        var text = Marshal.StringToHGlobalUni(reason);
        try
        {
            var ctx = new ReasonContext { Version = PowerRequestContextVersion, Flags = PowerRequestContextSimpleString, SimpleReasonString = text };
            var handle = PowerCreateRequest(ref ctx);
            if (handle == 0 || handle == -1)
            {
                return new Request(0);
            }

            if (!PowerSetRequest(handle, PowerRequestSystemRequired))
            {
                CloseHandle(handle);
                return new Request(0);
            }

            return new Request(handle);
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }
    }

    /// <summary>True while at least this request object is active (for tests and diagnostics).</summary>
    public static bool IsActive(IDisposable request) => request is Request { Handle: not 0 };

    private sealed class Request(nint handle) : IDisposable
    {
        public nint Handle { get; private set; } = handle;

        public void Dispose()
        {
            var h = Handle;
            Handle = 0;
            if (h != 0)
            {
                PowerClearRequest(h, PowerRequestSystemRequired);
                CloseHandle(h);
            }
        }
    }
}
