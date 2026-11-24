using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Portal.AiHost.Core.Store;

/// <summary>
/// The real path of an existing folder as Windows resolves it through an open handle: junctions, symbolic links,
/// subst drives and 8.3 short names all come out as the final path. Read-only.
/// </summary>
internal static class FinalPath
{
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint OpenExisting = 3;
    private const uint ShareAll = 7;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
#pragma warning disable SYSLIB1054 // string marshalling is fine here
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder buffer, uint size, uint flags);
#pragma warning restore SYSLIB1054

    /// <summary>The canonical path of an existing folder, or null when it cannot be resolved (or off Windows).</summary>
    public static string? Resolve(string folder)
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(folder))
        {
            return null;
        }

        try
        {
            using var handle = CreateFileW(folder, 0, ShareAll, 0, OpenExisting, FileFlagBackupSemantics, 0);
            if (handle.IsInvalid)
            {
                return null;
            }

            var sb = new StringBuilder(1024);
            var n = GetFinalPathNameByHandleW(handle, sb, (uint)sb.Capacity, 0);
            if (n == 0 || n >= sb.Capacity)
            {
                return null;
            }

            var path = sb.ToString();
            if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            {
                path = @"\\" + path[8..];
            }
            else if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                path = path[4..];
            }

            return Path.TrimEndingDirectorySeparator(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
