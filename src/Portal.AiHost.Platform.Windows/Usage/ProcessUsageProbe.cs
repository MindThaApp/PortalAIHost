using System.Diagnostics;
using System.Runtime.InteropServices;
using Portal.AiHost.Core.Runtime;

namespace Portal.AiHost.Platform.Windows.Usage;

/// <summary>
/// Reads an engine process cheaply: working set and processor time from the process itself, and graphics memory
/// from the Windows "GPU Process Memory" counters (all adapters summed). Anything that cannot be read counts as zero.
/// </summary>
public sealed partial class ProcessUsageProbe : IProcessUsageProbe, IDisposable
{
    private readonly Lock _gate = new();
    private int _gpuPid;
    private nint _query;
    private readonly List<nint> _counters = [];

    public ProcessUsage? Read(int processId)
    {
        try
        {
            using var p = Process.GetProcessById(processId);
            if (p.HasExited)
            {
                return null;
            }

            return new ProcessUsage(p.WorkingSet64, ReadVram(processId), p.TotalProcessorTime);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception
                                       or NotSupportedException)
        {
            return null;
        }
    }

    private long ReadVram(int pid)
    {
        lock (_gate)
        {
            try
            {
                if (_gpuPid != pid)
                {
                    CloseQuery();
                    OpenQuery(pid);
                }

                if (_query == 0 || _counters.Count == 0 || PdhCollectQueryData(_query) != 0)
                {
                    return 0;
                }

                long total = 0;
                foreach (var counter in _counters)
                {
                    if (PdhGetFormattedCounterValue(counter, PdhFmtLarge, out _, out var value) == 0 && value.Status == 0)
                    {
                        total += value.LargeValue;
                    }
                }

                return Math.Max(0, total);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
            {
                return 0;
            }
        }
    }

    private void OpenQuery(int pid)
    {
        _gpuPid = pid;
        if (PdhOpenQueryW(null, 0, out var query) != 0)
        {
            return;
        }

        _query = query;
        var wildcard = $"\\GPU Process Memory(pid_{pid}_*)\\Dedicated Usage";
        uint size = 0;
        PdhExpandWildCardPathW(null, wildcard, null, ref size, 0); // asks for the size (PDH_MORE_DATA)
        if (size == 0 || size > 1 << 20)
        {
            return;
        }

        var buffer = new char[size];
        if (PdhExpandWildCardPathW(null, wildcard, buffer, ref size, 0) != 0)
        {
            return;
        }

        foreach (var path in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (PdhAddEnglishCounterW(_query, path, 0, out var counter) == 0)
            {
                _counters.Add(counter);
            }
        }
    }

    private void CloseQuery()
    {
        if (_query != 0)
        {
            PdhCloseQuery(_query);
        }

        _query = 0;
        _counters.Clear();
        _gpuPid = 0;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            CloseQuery();
        }
    }

    private const uint PdhFmtLarge = 0x400;

    [StructLayout(LayoutKind.Explicit)]
    private struct PdhFmtCounterValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public long LargeValue;
    }

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhOpenQueryW(string? dataSource, nint userData, out nint query);

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhExpandWildCardPathW(string? dataSource, string wildcardPath, [Out] char[]? expandedPaths,
        ref uint bufferLength, uint flags);

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhAddEnglishCounterW(nint query, string path, nint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhGetFormattedCounterValue(nint counter, uint format, out uint type, out PdhFmtCounterValue value);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCloseQuery(nint query);
}
