using System.Runtime.InteropServices;
using Portal.AiHost.Core.Abstractions;

namespace Portal.AiHost.Platform.Windows.Power;

/// <summary>
/// The Windows checks for the ease-off rules. Both are single, cheap system calls: the shell's notification state
/// (full-screen game, video or presentation) and the power status (battery).
/// </summary>
public sealed partial class EaseOffSensors : IEaseOffSensors
{
    // QUERY_USER_NOTIFICATION_STATE
    private const int QunsBusy = 2;               // a full-screen program is in front
    private const int QunsRunningD3dFullScreen = 3;
    private const int QunsPresentationMode = 4;

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out int state);

    /// <summary>True for a full-screen program (not a maximized window): the shell reports busy, Direct3D full screen or presentation mode.</summary>
    public bool IsFullscreen() =>
        SHQueryUserNotificationState(out var state) == 0 && state is QunsBusy or QunsRunningD3dFullScreen or QunsPresentationMode;

    /// <summary>
    /// Not detected in this version: there is no cheap, reliable system-wide graphics load reading, so this rule
    /// never fires on its own. The setting is kept so it works once a reading is added.
    /// </summary>
    public bool IsGpuBusy() => false;

    public bool IsOnBattery() => PowerStatus.IsOnBattery();
}
