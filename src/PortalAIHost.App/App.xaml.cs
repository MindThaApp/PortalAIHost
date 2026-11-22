using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using PortalAIHost.App.Services;

namespace PortalAIHost.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => e.Handled = true;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppServices.Build();
        _window = new MainWindow();
        _window.Activate();
        // A new install opens the welcome first, so the main window stays hidden until that is decided.
        if (!AppServices.Shell.State.FirstRunDone) _window.HideForWelcome();
        // A second launch is redirected here: bring the window forward.
        AppInstance.GetCurrent().Activated += (_, _) => AppServices.Ui.Post(() => _window?.Activate());
        _ = _window.StartAsync();
    }
}
