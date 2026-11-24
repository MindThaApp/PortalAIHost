using Microsoft.UI.Xaml;

namespace PortalAIHost.App.Services;

/// <summary>Opens a window, activating it unless a test run asked for windows that never take the keyboard.</summary>
public static class WindowShow
{
    public static void Open(Window window)
    {
        if (AppOptions.NoActivate) window.AppWindow.Show(false);
        else window.Activate();
    }
}
