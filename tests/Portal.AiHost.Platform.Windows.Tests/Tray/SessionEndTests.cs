using System.Runtime.InteropServices;
using Portal.AiHost.Platform.Windows.Tray;

namespace Portal.AiHost.Platform.Windows.Tests.Tray;

public sealed class SessionEndTests
{
    private const uint WmQueryEndSession = 0x0011;
    private const uint WmEndSession = 0x0016;

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint hwnd, uint msg, nint wParam, nint lParam);

    [Fact]
    public void Hidden_window_allows_shutdown_and_raises_session_ending_synchronously()
    {
        // No tray icon: only the hidden, off-screen message window.
        using var tray = TrayIcon.Start(showIcon: false);
        var calls = new List<bool>();
        tray.SessionEnding += (_, closeApp) =>
        {
            Thread.Sleep(100); // A slow clean stop must finish before the message returns.
            calls.Add(closeApp);
        };

        Assert.Equal(1, SendMessage(tray.WindowHandle, WmQueryEndSession, 0, 0));
        SendMessage(tray.WindowHandle, WmEndSession, 0, 0); // Shutdown cancelled: nothing happens.
        Assert.Empty(calls);
        SendMessage(tray.WindowHandle, WmEndSession, 1, 0);
        SendMessage(tray.WindowHandle, WmEndSession, 1, 1); // ENDSESSION_CLOSEAPP (update)
        Assert.Equal([false, true], calls);
    }
}
