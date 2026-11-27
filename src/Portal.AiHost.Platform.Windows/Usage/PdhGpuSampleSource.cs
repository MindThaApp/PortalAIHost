using System.Runtime.InteropServices;
using Portal.AiHost.Core.Remote;

namespace Portal.AiHost.Platform.Windows.Usage;

/// <summary>
/// Reads the "GPU Engine" utilisation counters through PDH (P/Invoke, so it is trim and AOT safe). One query with a wildcard
/// counter is kept open and reopened now and then so new processes appear. Returns null when the counters are missing.
/// </summary>
public sealed partial class PdhGpuSampleSource : IGpuSampleSource, IDisposable
{
    private const string CounterPath = @"\GPU Engine(*)\Utilization Percentage";
    private const uint PdhFmtDouble = 0x200;
    private const uint PdhMoreData = 0x800007D2;
    private static readonly TimeSpan Reopen = TimeSpan.FromSeconds(60);

    private nint _query, _counter;
    private DateTime _openedAt;
    private bool _unavailable;
    private byte[] _buffer = new byte[16 * 1024];

    [StructLayout(LayoutKind.Sequential)]
    private struct FmtDouble { public uint Status; public double Value; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ItemDouble { public nint Name; public FmtDouble Value; }

    [LibraryImport("pdh.dll")] private static partial uint PdhOpenQueryW(nint source, nint user, out nint query);
    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial uint PdhAddEnglishCounterW(nint query, string path, nint user, out nint counter);
    [LibraryImport("pdh.dll")] private static partial uint PdhCollectQueryData(nint query);
    [LibraryImport("pdh.dll")] private static partial uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint size, ref uint count, nint buffer);
    [LibraryImport("pdh.dll")] private static partial uint PdhCloseQuery(nint query);

    public IReadOnlyList<GpuProcessLoad>? Sample()
    {
        if (_unavailable)
        {
            return null;
        }

        try
        {
            if (_query != 0 && DateTime.UtcNow - _openedAt > Reopen)
            {
                Close();
            }

            if (_query == 0)
            {
                if (!Open())
                {
                    _unavailable = true;
                    return null;
                }

                // A rate counter needs two collections; the first sample after opening has nothing to report.
                return [];
            }

            return PdhCollectQueryData(_query) == 0 ? Read() : [];
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            _unavailable = true;
            return null;
        }
    }

    private bool Open()
    {
        if (PdhOpenQueryW(0, 0, out _query) != 0)
        {
            _query = 0;
            return false;
        }

        if (PdhAddEnglishCounterW(_query, CounterPath, 0, out _counter) != 0)
        {
            Close();
            return false;
        }

        _openedAt = DateTime.UtcNow;
        _ = PdhCollectQueryData(_query);
        return true;
    }

    private unsafe List<GpuProcessLoad> Read()
    {
        var result = new List<GpuProcessLoad>();
        uint size = (uint)_buffer.Length, count = 0;
        fixed (byte* p = _buffer)
        {
            var rc = PdhGetFormattedCounterArrayW(_counter, PdhFmtDouble, ref size, ref count, (nint)p);
            if (rc == PdhMoreData)
            {
                _buffer = new byte[size];
                return result;
            }

            if (rc != 0)
            {
                return result;
            }

            var items = (ItemDouble*)p;
            var perPid = new Dictionary<int, double>();
            for (var i = 0; i < count; i++)
            {
                var name = Marshal.PtrToStringUni(items[i].Name);
                if (name is null || items[i].Value.Status != 0 || !name.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase)
                    || !name.StartsWith("pid_", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var end = name.IndexOf('_', 4);
                if (end > 4 && int.TryParse(name.AsSpan(4, end - 4), out var pid))
                {
                    perPid[pid] = perPid.GetValueOrDefault(pid) + items[i].Value.Value;
                }
            }

            foreach (var (pid, percent) in perPid)
            {
                result.Add(new GpuProcessLoad(pid, percent));
            }
        }

        return result;
    }

    private void Close()
    {
        if (_query != 0)
        {
            _ = PdhCloseQuery(_query);
        }

        _query = _counter = 0;
    }

    public void Dispose() => Close();
}
