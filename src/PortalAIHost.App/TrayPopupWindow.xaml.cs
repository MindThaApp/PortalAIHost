using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using PortalAIHost.App.Services;
using PortalAIHost.App.ViewModels;
using Windows.Graphics;
using WinRT.Interop;

namespace PortalAIHost.App;

/// <summary>
/// The tray pop-up (design guide 4.4): borderless, always on top, not in Alt+Tab. Esc closes it; losing focus hides
/// it after a 100 ms grace (so a second click on the icon toggles it closed); it opens toward the screen interior on
/// the icon's monitor; the host is read every 1.5 s only while it is shown.
/// </summary>
public sealed partial class TrayPopupWindow : Window
{
    private const int WidthDip = 400;
    private const int MarginDip = 8;
    private const int MaxHeightDip = 560;
    private static readonly TimeSpan FocusGrace = TimeSpan.FromMilliseconds(100);

    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _graceTimer;
    private readonly Windows.UI.ViewManagement.UISettings _ui = new();
    private readonly Windows.UI.ViewManagement.AccessibilitySettings _access = new();
    private readonly IntPtr _hwnd;
    private bool _shown;

    public TrayPopupWindow()
    {
        ViewModel = new TrayPopupViewModel(AppServices.Host, AppServices.Ui, AppServices.Strings);
        ViewModel.OpenApp = () => { Hide(); OpenAppRequested?.Invoke(); };
        InitializeComponent();
        Title = AppServices.Strings.Get("TrayWindowTitle");
        _hwnd = WindowNative.GetWindowHandle(this);
        Root.FlowDirection = System.Globalization.CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft
            ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        Root.KeyboardAccelerators.Add(Escape());
        if (!_access.HighContrast) SystemBackdrop = new DesktopAcrylicBackdrop();
        else Root.Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];

        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsAlwaysOnTop = true;
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
        }
        AppWindow.IsShownInSwitchers = false;
        var round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(_hwnd, 33, ref round, sizeof(int));

        _graceTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _graceTimer.Interval = FocusGrace;
        _graceTimer.IsRepeating = false;
        _graceTimer.Tick += (_, _) => Hide();
        Activated += OnActivated;
    }

    public TrayPopupViewModel ViewModel { get; }

    /// <summary>Raised when the user chose Open app; the owner of this window shows the main window.</summary>
    public event Action? OpenAppRequested;

    /// <summary>Raised after the pop-up hid itself.</summary>
    public event Action? Hidden;

    /// <summary>True while the pop-up is on screen.</summary>
    public bool IsShown => _shown;

    /// <summary>Last time the pop-up hid, so the click that deactivated it does not reopen it at once.</summary>
    public DateTime LastHiddenUtc { get; private set; } = DateTime.MinValue;

    private KeyboardAccelerator Escape()
    {
        var k = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        k.Invoked += (_, e) =>
        {
            e.Handled = true;
            Hide();
        };
        return k;
    }

    /// <summary>Shows the pop-up next to the icon at (x, y) in screen pixels, on that monitor.</summary>
    public void ShowAt(int x, int y)
    {
        _graceTimer.Stop();
        var display = DisplayArea.GetFromPoint(new PointInt32(x, y), DisplayAreaFallback.Nearest);
        var scale = Math.Max(1.0, GetDpiForWindow(_hwnd) / 96.0);
        var width = (int)Math.Round(WidthDip * scale);
        Root.Measure(new Windows.Foundation.Size(WidthDip, double.PositiveInfinity));
        var height = (int)Math.Round(Math.Min(MaxHeightDip, Math.Max(160, Root.DesiredSize.Height)) * scale);
        var (px, py) = TrayPlacement.Place((x, y), ToBox(display.OuterBounds), ToBox(display.WorkArea), width, height,
            (int)Math.Round(MarginDip * scale));
        AppWindow.MoveAndResize(new RectInt32(px, py, width, height));
        ViewModel.Show(); // reading the host starts only now
        _shown = true;
        AppWindow.Show();
        Activate();
        PlayOpen();
        PopupTiming.Shown();
    }

    /// <summary>Hides the pop-up and stops reading the host.</summary>
    public void Hide()
    {
        _graceTimer.Stop();
        if (!_shown) return;
        _shown = false;
        LastHiddenUtc = DateTime.UtcNow;
        ViewModel.Hide();
        AppWindow.Hide();
        Hidden?.Invoke();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (!_shown) return;
        if (e.WindowActivationState == WindowActivationState.Deactivated) _graceTimer.Start();
        else _graceTimer.Stop();
    }

    private void PlayOpen()
    {
        // The shared motion token; skipped when Windows animations are off or the user chose reduced motion.
        if (AppearanceMath.ReducedMotion(AppServices.Shell.State.Appearance.Motion, _ui.AnimationsEnabled))
        {
            Root.Opacity = 1;
            Slide.Y = 0;
            return;
        }
        var spline = new KeySpline
        {
            ControlPoint1 = new Windows.Foundation.Point(Motion.CurveX1, Motion.CurveY1),
            ControlPoint2 = new Windows.Foundation.Point(Motion.CurveX2, Motion.CurveY2),
        };
        var board = new Storyboard();
        board.Children.Add(Animate(Root, "Opacity", 0, 1, spline));
        board.Children.Add(Animate(Slide, "Y", 8, 0, spline));
        board.Begin();
    }

    private static DoubleAnimationUsingKeyFrames Animate(DependencyObject target, string property, double from, double to, KeySpline spline)
    {
        var a = new DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = from });
        a.KeyFrames.Add(new SplineDoubleKeyFrame { KeyTime = Motion.Base, Value = to, KeySpline = spline });
        Storyboard.SetTarget(a, target);
        Storyboard.SetTargetProperty(a, property);
        return a;
    }

    private static Box ToBox(RectInt32 r) => new(r.X, r.Y, r.Width, r.Height);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}

/// <summary>
/// Measures the time from process start to the pop-up being visible (cold start). Debug builds append one line to a
/// file in the temp folder; release builds do nothing. The number comes from a test run, never a live check.
/// </summary>
public static class PopupTiming
{
    private static bool _reported;

    /// <summary>True when this process was started by a tray click rather than by opening the main window.</summary>
    public static bool ColdPopup { get; set; }

    public static void Shown()
    {
#if DEBUG
        if (_reported || !ColdPopup) return;
        _reported = true;
        try
        {
            var ms = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "PortalAIHost-popup-timing.log"),
                $"{DateTime.Now:O} cold start to pop-up visible: {ms:0} ms{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
        }
#endif
    }
}
