using System.Diagnostics;

namespace PortalAIHost.App.Services;

/// <summary>
/// Opens a folder in Explorer. Only shows a local folder that exists and is not a link; starts the Windows Explorer
/// by its full path, never by a name searched on the path. No shell verbs that change anything.
/// </summary>
public sealed class ExplorerFolderOpener : IFolderOpener
{
    public void Open(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new IOException("Only a folder on a local drive can be opened.");
        }

        var info = new DirectoryInfo(path);
        if (!info.Exists)
        {
            throw new IOException("That folder is not there (yet).");
        }

        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null)
        {
            throw new IOException("That folder is a link to another place, so it was not opened.");
        }

        try
        {
            if (new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network)
            {
                throw new IOException("Only a folder on a local drive can be opened.");
            }
        }
        catch (ArgumentException)
        {
            throw new IOException("Only a folder on a local drive can be opened.");
        }

        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var start = new ProcessStartInfo(explorer) { UseShellExecute = false };
        start.ArgumentList.Add(path);
        Process.Start(start)?.Dispose();
    }
}
