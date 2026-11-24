using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Portal.AiHost.Engine;

/// <summary>
/// Windows Job Object with KILL_ON_JOB_CLOSE. Every child engine is assigned to one, so when the
/// host exits or crashes the OS kills the engine and no 20 GB process is orphaned.
/// </summary>
public sealed partial class JobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private const uint JobObjectLimitJobMemory = 0x0200;
    private const int JobObjectCpuRateControlInformation = 15;
    private const uint CpuRateControlEnable = 0x1;
    private const uint CpuRateControlHardCap = 0x4;

    private readonly nint _handle;
    private bool _disposed;

    public JobObject()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Job objects are Windows only.");
        }

        _handle = CreateJobObjectW(nint.Zero, null);
        if (_handle == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            var err = Marshal.GetLastWin32Error();
            CloseHandle(_handle);
            throw new Win32Exception(err);
        }
    }

    /// <summary>
    /// Caps the memory every process in the job may commit together (JobMemoryLimit), or removes the cap when
    /// <paramref name="bytes"/> is null or not positive. Keeps kill-on-close. Allocations past the cap fail inside
    /// the engine; nothing is deleted. Returns false when the OS refuses.
    /// </summary>
    public bool SetMemoryLimit(long? bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        if (bytes is > 0)
        {
            info.BasicLimitInformation.LimitFlags |= JobObjectLimitJobMemory;
            info.JobMemoryLimit = (nuint)bytes.Value;
        }

        var ok = SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, ref info,
            (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
        if (ok)
        {
            MemoryLimitBytes = bytes is > 0 ? bytes : null;
        }

        return ok;
    }

    /// <summary>
    /// Caps the processor share of every process in the job (hard cap), as hundredths of a percent of the whole PC
    /// (1 to 10000), or removes the cap when <paramref name="rate"/> is null. Returns false when the OS refuses.
    /// </summary>
    public bool SetCpuRate(int? rate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var info = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION();
        if (rate is > 0)
        {
            info.ControlFlags = CpuRateControlEnable | CpuRateControlHardCap;
            info.CpuRate = (uint)Math.Clamp(rate.Value, 1, 10000);
        }

        var ok = SetInformationJobObject(_handle, JobObjectCpuRateControlInformation, ref info,
            (uint)Marshal.SizeOf<JOBOBJECT_CPU_RATE_CONTROL_INFORMATION>());
        if (ok)
        {
            CpuRate = rate is > 0 ? Math.Clamp(rate.Value, 1, 10000) : null;
        }

        return ok;
    }

    /// <summary>The processor cap in force (hundredths of a percent), or null when none is set.</summary>
    public int? CpuRate { get; private set; }

    /// <summary>The memory cap in force, or null when none is set.</summary>
    public long? MemoryLimitBytes { get; private set; }

    /// <summary>Puts a running process in the job. Throws if the OS refuses.</summary>
    public void Assign(Process process)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!AssignProcessToJobObject(_handle, process.Handle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    /// <summary>Closing the handle kills every process still in the job.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CloseHandle(_handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
    {
        public uint ControlFlags;
        public uint CpuRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int infoClass, ref JOBOBJECT_CPU_RATE_CONTROL_INFORMATION info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
