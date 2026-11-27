using System.Runtime.InteropServices;

namespace Portal.AiHost.Core.Store;

/// <summary>
/// Sends one host-owned file to the Recycle Bin. Never deletes permanently: when the file cannot be recycled
/// (the Bin is off for that drive, the drive has no Bin, or the file is bigger than the Bin) it refuses with a
/// <see cref="StoreSafetyException"/> and leaves the file in place. Faked in tests.
/// </summary>
public interface IFileRecycler
{
    /// <summary>Throws <see cref="StoreSafetyException"/> when <paramref name="fullPath"/> could not go to the Bin.</summary>
    void AssertCanRecycle(string fullPath, long sizeBytes);

    /// <summary>Moves the file to the Recycle Bin, after the same checks as <see cref="AssertCanRecycle"/>.</summary>
    void Recycle(string fullPath, long sizeBytes);
}

/// <summary>What the Recycle Bin of the drive holding a path accepts.</summary>
/// <param name="Enabled">False when files on that drive would be deleted at once (no Bin, or the Bin is switched off).</param>
/// <param name="MaxBytes">Largest total the Bin holds on that drive, or null when unknown.</param>
public readonly record struct RecycleBinState(bool Enabled, long? MaxBytes);

public interface IRecycleBinPolicy
{
    RecycleBinState Query(string fullPath);
}

/// <summary>Reads the per-drive Recycle Bin settings of the signed-in user.</summary>
public sealed class WindowsRecycleBinPolicy : IRecycleBinPolicy
{
    public static WindowsRecycleBinPolicy Instance { get; } = new();

    private const string BitBucket = @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket";

    public RecycleBinState Query(string fullPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new RecycleBinState(false, null);
        }

        var root = Path.GetPathRoot(Path.GetFullPath(fullPath));
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return new RecycleBinState(false, null); // network paths have no Bin
        }

        DriveInfo drive;
        try
        {
            drive = new DriveInfo(root);
            if (drive.DriveType != DriveType.Fixed)
            {
                return new RecycleBinState(false, null); // removable and network drives delete at once
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return new RecycleBinState(false, null);
        }

        using (var policies = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer"))
        {
            if (policies?.GetValue("NoRecycleFiles") is int noRecycle && noRecycle != 0)
            {
                return new RecycleBinState(false, null);
            }
        }

        var volume = new char[64];
        if (!GetVolumeNameForVolumeMountPointW(root, volume, volume.Length))
        {
            return new RecycleBinState(false, null);
        }

        var name = new string(volume).TrimEnd('\0');
        var open = name.IndexOf('{');
        var close = name.IndexOf('}');
        long defaultMax = (long)(drive.TotalSize * 0.05); // conservative stand-in when the drive has no settings yet
        if (open < 0 || close < open)
        {
            return new RecycleBinState(true, defaultMax);
        }

        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(BitBucket + @"\Volume\" + name[open..(close + 1)]);
        if (key is null)
        {
            return new RecycleBinState(true, defaultMax);
        }

        if (key.GetValue("NukeOnDelete") is int nuke && nuke != 0)
        {
            return new RecycleBinState(false, null);
        }

        return key.GetValue("MaxCapacity") is int megabytes && megabytes > 0
            ? new RecycleBinState(true, megabytes * 1024L * 1024L)
            : new RecycleBinState(true, defaultMax);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, [Out] char[] volumeName, int length);
}

/// <summary>
/// Recycles through the shell. Checks the drive's Bin first, so the shell never falls back to a permanent delete.
/// </summary>
public sealed class ShellRecycler(IRecycleBinPolicy policy, Action<string>? sendToBin = null) : IFileRecycler
{
    public static ShellRecycler Default { get; } = new(WindowsRecycleBinPolicy.Instance);

    private readonly Action<string> _sendToBin = sendToBin ?? SendToBin;

    public void AssertCanRecycle(string fullPath, long sizeBytes)
    {
        var state = policy.Query(fullPath);
        if (!state.Enabled)
        {
            throw new StoreSafetyException(
                "The Recycle Bin is off for this drive, so removing would delete the file for good. Nothing was removed. You can delete the file from its folder yourself.");
        }

        if (state.MaxBytes is { } max && sizeBytes > max)
        {
            throw new StoreSafetyException(
                "This model is bigger than the Recycle Bin of its drive, so removing would delete it for good. Nothing was removed. You can delete the file from its folder yourself.");
        }
    }

    public void Recycle(string fullPath, long sizeBytes)
    {
        AssertCanRecycle(fullPath, sizeBytes);
        _sendToBin(fullPath);
        if (File.Exists(fullPath))
        {
            throw new IOException($"'{fullPath}' could not be moved to the Recycle Bin.");
        }
    }

    private static void SendToBin(string fullPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new StoreSafetyException("The Recycle Bin is only available on Windows.");
        }

        var op = new SHFILEOPSTRUCTW
        {
            wFunc = FO_DELETE,
            pFrom = fullPath + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT | FOF_NO_UI_WANTNUKEWARNING,
        };
        var result = SHFileOperationW(ref op);
        if (result != 0 || op.fAnyOperationsAborted)
        {
            throw new IOException($"The Recycle Bin did not take '{fullPath}' (code {result}). The file was left in place.");
        }
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    // With FOF_WANTNUKEWARNING the shell aborts instead of silently deleting for good if the Bin turns out to be
    // unavailable after the checks above; the abort shows up as fAnyOperationsAborted.
    private const ushort FOF_NO_UI_WANTNUKEWARNING = FOF_WANTNUKEWARNING;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW lpFileOp);
}
