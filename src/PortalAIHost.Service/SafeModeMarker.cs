using System.Globalization;
using Portal.AiHost.Core.Settings;

namespace PortalAIHost.Service;

/// <summary>
/// The <c>.running</c> marker in LocalState. It exists while the host runs and holds the number of unclean exits
/// in a row before this start. Found at start-up means the last run did not quit cleanly; two in a row means
/// safe mode (nothing loads automatically). A clean quit removes it.
/// </summary>
public sealed class SafeModeMarker
{
    public const string FileName = ".running";
    public const int SafeModeAfter = 2;

    private readonly string _path;

    private SafeModeMarker(string path, int uncleanExits)
    {
        _path = path;
        UncleanExits = uncleanExits;
    }

    public int UncleanExits { get; }

    public bool SafeMode => UncleanExits >= SafeModeAfter;

    /// <summary>Reads the previous marker (if any) and writes this run's marker.</summary>
    public static SafeModeMarker Begin(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataRoot);
        Directory.CreateDirectory(dataRoot);
        var path = Path.Combine(dataRoot, FileName);
        var unclean = 0;
        if (File.Exists(path))
        {
            var previous = 0;
            try
            {
                var text = File.ReadAllText(path).Trim();
                _ = int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out previous);
            }
            catch (IOException)
            {
            }

            unclean = Math.Min(previous, 1000) + 1;
        }

        AtomicFile.WriteAllBytes(path, System.Text.Encoding.ASCII.GetBytes(unclean.ToString(CultureInfo.InvariantCulture)));
        return new SafeModeMarker(path, unclean);
    }

    /// <summary>Clean quit: removes this run's marker (only the host's own <c>.running</c> file).</summary>
    public void EndClean()
    {
        if (string.Equals(Path.GetFileName(_path), FileName, StringComparison.Ordinal) && File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
