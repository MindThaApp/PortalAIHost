using System.Runtime.InteropServices;
using Portal.AiHost.Core.Remote;

namespace Portal.AiHost.Platform.Windows.Usage;

/// <summary>
/// Gaming mode's detector: Windows reports a full-screen Direct3D program (a game) or any other full-screen
/// presentation through the shell. Heavy GPU use without a full-screen window is not detected here; the
/// <see cref="IBusyDetector"/> seam lets a GPU-load detector be added without touching the callers.
/// </summary>
public sealed partial class FullScreenGameDetector : IBusyDetector
{
    private const int NotPresent = 0, Busy = 2, RunningD3DFullScreen = 3, PresentationMode = 4;

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out int state);

    public bool IsBusy()
    {
        try
        {
            return SHQueryUserNotificationState(out var state) == 0 && state is Busy or RunningD3DFullScreen or PresentationMode;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return false;
        }
    }
}
