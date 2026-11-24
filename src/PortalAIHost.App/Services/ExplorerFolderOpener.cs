using System.Diagnostics;

namespace PortalAIHost.App.Services;

/// <summary>Opens a folder in Explorer. Only shows a folder that exists; no shell verbs that change anything.</summary>
public sealed class ExplorerFolderOpener : IFolderOpener
{
    public void Open(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path))
        {
            throw new IOException("That folder is not there (yet).");
        }

        var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        info.ArgumentList.Add(path);
        Process.Start(info)?.Dispose();
    }
}
