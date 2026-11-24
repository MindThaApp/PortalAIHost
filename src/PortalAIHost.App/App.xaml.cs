using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using PortalAIHost.App.Services;

namespace PortalAIHost.App;

public partial class App : Application
{
    /// <summary>How long a pop-up-only process stays hidden before it exits, so a second tray click is instant.</summary>
    private static readonly TimeSpan PopupLinger = TimeSpan.FromSeconds(60);
    /// <summary>A click that just closed the pop-up (by taking its focus) must not reopen it.</summary>
    private static readonly TimeSpan ToggleWindow = TimeSpan.FromMilliseconds(300);

    private MainWindow? _window;
    private TrayPopupWindow? _popup;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _lingerTimer;

    public App()
    {
        InitializeComponent();
#if DEBUG
        UnhandledException += (_, e) => DebugLog.Write("UnhandledException: " + e.Message + Environment.NewLine + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => DebugLog.Write("AppDomain.UnhandledException: " + e.ExceptionObject);
#endif
        UnhandledException += (_, e) => e.Handled = true;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppServices.Build();
        AppServices.Host.TrayPopup += p => AppServices.Ui.Post(() => TogglePopup(p.X, p.Y));
        // A second launch is redirected here: a tray click shows the pop-up, anything else brings the window forward.
        AppInstance.GetCurrent().Activated += (_, e) => AppServices.Ui.Post(() => OnRedirected(e));
        if (AppOptions.TrayPopup is { } at)
        {
            // Started by a tray click: show only the pop-up. The main window is made when Open app is chosen.
            PopupTiming.ColdPopup = true;
            ShowPopup(at.X, at.Y);
            return;
        }

        EnsureMainWindow();
    }

    private void OnRedirected(AppActivationArguments e)
    {
        var text = e.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch ? launch.Arguments : null;
        if (TrayPopupArgs.Find(text) is { } at) TogglePopup(at.X, at.Y);
        else EnsureMainWindow().Activate();
    }

    private MainWindow EnsureMainWindow()
    {
        if (_window is { } existing) return existing;
        var window = new MainWindow();
        _window = window;
        window.Closed += (_, _) =>
        {
            _window = null;
            _popup?.Close(); // the process ends with its last window
            _popup = null;
        };
        WindowShow.Open(window);
        // A new install opens the welcome first, so the main window stays hidden until that is decided.
        if (!AppServices.Shell.State.FirstRunDone) window.HideForWelcome();
        _ = window.StartAsync();
        return window;
    }

    private void TogglePopup(int x, int y)
    {
        if (_popup is { IsShown: true } open)
        {
            open.Hide();
            return;
        }

        if (_popup is not null && DateTime.UtcNow - _popup.LastHiddenUtc < ToggleWindow) return;
        ShowPopup(x, y);
    }

    private void ShowPopup(int x, int y)
    {
        _lingerTimer?.Stop();
        if (_popup is null)
        {
            _popup = new TrayPopupWindow();
            _popup.OpenAppRequested += () => EnsureMainWindow().Activate();
            _popup.Hidden += OnPopupHidden;
        }

        _popup.ShowAt(x, y);
    }

    private void OnPopupHidden()
    {
        // With no main window this process exists only for the pop-up: leave after a short while.
        if (_window is not null) return;
        _lingerTimer ??= Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _lingerTimer.Interval = PopupLinger;
        _lingerTimer.IsRepeating = false;
        _lingerTimer.Tick -= OnLingerElapsed;
        _lingerTimer.Tick += OnLingerElapsed;
        _lingerTimer.Start();
    }

    private void OnLingerElapsed(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (_window is null && _popup is { IsShown: false }) Exit();
    }
}
