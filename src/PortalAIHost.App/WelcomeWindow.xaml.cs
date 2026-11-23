using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using PortalAIHost.App.Services;
using PortalAIHost.App.ViewModels;

namespace PortalAIHost.App;

/// <summary>
/// The welcome: its own window with a hero, at most three steps with dots, Skip on every step and Back/Next.
/// Closing it in any way (Finish, Skip, Esc, the close button) marks the welcome as done; choices made on the way stay.
/// </summary>
public sealed partial class WelcomeWindow : Window
{
    private const int DesignWidth = 720;
    private const int DesignHeight = 620;
    private const int ArriveMs = 333; // the "slow" motion token, used for welcome steps

    private readonly ShellViewModel _shell = AppServices.Shell;
    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();
    private readonly AppearancePrefs _prefs;
    private readonly StackPanel[] _panels;
    private int _lastStep;

    public WelcomeWindow()
    {
        ViewModel = new FirstRunViewModel(_shell, AppServices.Ui, AppServices.Strings);
        InitializeComponent();
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "portalaihost.ico"));
        // A plain drag strip with only the close button; the window cannot be resized, minimised or maximised.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragStrip);
        if (AppWindow.Presenter is OverlappedPresenter fixedSize)
        {
            fixedSize.IsResizable = false;
            fixedSize.IsMaximizable = false;
            fixedSize.IsMinimizable = false;
        }
        _prefs = AppOptions.WithOverrides(_shell.State.Appearance);
        _panels = [Panel0, Panel1, Panel2];
        Title = AppServices.Strings.Get("Vm_WelcomeTitle");

        Root.RequestedTheme = _prefs.Theme switch
        {
            ThemeChoice.Light => ElementTheme.Light,
            ThemeChoice.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        var plain = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast || _prefs.Material == MaterialChoice.Solid;
        SystemBackdrop = plain ? null : _prefs.Material == MaterialChoice.Acrylic
            ? new DesktopAcrylicBackdrop()
            : new MicaBackdrop();
        Root.Background = plain ? (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"] : null;
        ApplyTextScale(AppOptions.TextScale ?? AppearanceMath.BodyFontSize(_prefs.TextStep) / 14.0);
        PlaceWindow();

        ViewModel.PropertyChanged += OnViewModelChanged;
        ViewModel.CloseRequested += () => Close();
        Closed += OnClosed;
        AddKeys();
        // When the page is taller than the window (large text), the body takes Tab so the arrow keys and Page Down can scroll it.
        BodyScroll.LayoutUpdated += (_, _) => BodyScroll.IsTabStop = BodyScroll.ScrollableHeight > 0;
        UpdateDots();
        Root.ActualThemeChanged += (_, _) => ApplyTitleBarColours();
        Root.Loaded += async (_, _) =>
        {
            ApplyTitleBarColours();
            LayoutFooter();
            AutomationProperties_SetDots();
            NextButton.Focus(FocusState.Programmatic); // the first thing to act on, so Enter and Narrator start on Next
            await ViewModel.LoadAsync();
        };
    }

    public FirstRunViewModel ViewModel { get; }

    private bool ReducedMotion => AppearanceMath.ReducedMotion(_prefs.Motion, _uiSettings.AnimationsEnabled);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>About 720 x 620, never larger than the work area, centred on the screen it opens on.</summary>
    private void PlaceWindow()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = Math.Max(96u, GetDpiForWindow(hwnd)) / 96.0;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var w = (int)Math.Min(DesignWidth * scale, area.Width - 32 * scale);
        var h = (int)Math.Min(DesignHeight * scale, area.Height - 32 * scale);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
        if (AppOptions.OffScreen)
        {
            AppWindow.Move(new Windows.Graphics.PointInt32(-3000, 100));
            return;
        }
        AppWindow.Move(new Windows.Graphics.PointInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2));
    }

    /// <summary>Every text size follows the text factor: body, row titles, captions, headings and buttons.</summary>
    private void ApplyTextScale(double k)
    {
        BodyScroll.FontSize = 14 * k;
        foreach (var b in new Control[] { SkipButton, RecommendedButton, BackButton, NextButton, DownloadButton, LaterButton, RetryButton })
            b.FontSize = 14 * k;
        HeroTitle.FontSize = 36 * k;
        Head0.FontSize = 14 * k;
        Head1.FontSize = 28 * k;
        Head2.FontSize = 28 * k;
    }

    /// <summary>Caption buttons follow the app theme, as in the main window.</summary>
    private void ApplyTitleBarColours()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        var tb = AppWindow.TitleBar;
        if (new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
        {
            tb.ButtonBackgroundColor = null; tb.ButtonInactiveBackgroundColor = null; tb.ButtonForegroundColor = null;
            tb.ButtonInactiveForegroundColor = null; tb.ButtonHoverBackgroundColor = null; tb.ButtonHoverForegroundColor = null;
            tb.ButtonPressedBackgroundColor = null; tb.ButtonPressedForegroundColor = null;
            return;
        }
        var dark = Root.ActualTheme == ElementTheme.Dark;
        var fg = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        tb.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        tb.ButtonForegroundColor = fg;
        tb.ButtonInactiveForegroundColor = dark ? Windows.UI.Color.FromArgb(0xFF, 0x9A, 0x9A, 0x9A) : Windows.UI.Color.FromArgb(0xFF, 0x70, 0x70, 0x70);
        tb.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x20, 255, 255, 255) : Windows.UI.Color.FromArgb(0x18, 0, 0, 0);
        tb.ButtonHoverForegroundColor = fg;
        tb.ButtonPressedBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x38, 255, 255, 255) : Windows.UI.Color.FromArgb(0x30, 0, 0, 0);
        tb.ButtonPressedForegroundColor = fg;
    }

    private void OnFooterSizeChanged(object sender, SizeChangedEventArgs e) => LayoutFooter();

    /// <summary>When the left and right button groups no longer fit on one line (large text), the left group moves to its own line. Nothing is clipped.</summary>
    private void LayoutFooter()
    {
        FooterLeft.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        FooterRight.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var room = Footer.ActualWidth - Footer.Padding.Left - Footer.Padding.Right - Footer.ColumnSpacing;
        if (room <= 0) return;
        var stacked = FooterLeft.DesiredSize.Width + FooterRight.DesiredSize.Width > room;
        var row = stacked ? 1 : 0;
        if (Grid.GetRow(FooterLeft) == row) return;
        Grid.SetRow(FooterLeft, row);
        Grid.SetColumnSpan(FooterLeft, stacked ? 2 : 1);
    }

    private void AddKeys()
    {
        // Esc means Skip for now; Enter means Next, unless a button or switch has the focus and takes Enter itself.
        var esc = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        esc.Invoked += (_, e) => { e.Handled = true; ViewModel.SkipCommand.Execute(null); };
        Root.KeyboardAccelerators.Add(esc);
        Root.KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;
        var enter = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Enter };
        enter.Invoked += (_, e) =>
        {
            if (Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Root.XamlRoot) is Button or ToggleSwitch or HyperlinkButton) return;
            e.Handled = true;
            ViewModel.NextCommand.Execute(null);
        };
        Root.KeyboardAccelerators.Add(enter);
    }

    /// <summary>Tells a screen reader which step this is and its heading, since focus moves to a button rather than the heading.</summary>
    private void AnnounceStep(int step)
    {
        TextBlock[] heads = [HeroTitle, Head1, Head2];
        var text = heads[Math.Clamp(step, 0, heads.Length - 1)].Text;
        var line = step == FirstRunViewModel.StepWelcome ? text : ViewModel.StepLine + ". " + text;
        var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(heads[Math.Clamp(step, 0, heads.Length - 1)])
                   ?? Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(heads[Math.Clamp(step, 0, heads.Length - 1)]);
        peer?.RaiseNotificationEvent(
            Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationKind.ActionCompleted,
            Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationProcessing.ImportantMostRecent,
            line, "WelcomeStep");
    }

    private void AutomationProperties_SetDots() =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Dots, ViewModel.StepLine);

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(FirstRunViewModel.Step):
                UpdateDots();
                AutomationProperties_SetDots();
                LayoutFooter();
                Arrive(ViewModel.Step, ViewModel.Step >= _lastStep ? 1 : -1);
                _lastStep = ViewModel.Step;
                break;
        }
    }

    /// <summary>Dots sit top-left and are hidden on the greeting; the current step is a longer pill, not only a different shade.</summary>
    private void UpdateDots()
    {
        Dots.Visibility = ViewModel.Step == FirstRunViewModel.StepWelcome ? Visibility.Collapsed : Visibility.Visible;
        Grid[] dots = [Dot0, Dot1, Dot2];
        Border[] on = [Dot0On, Dot1On, Dot2On];
        for (var i = 0; i < dots.Length; i++)
        {
            dots[i].Width = i == ViewModel.Step ? 20 : 8;
            on[i].Visibility = i == ViewModel.Step ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>The new step fades in while sliding 24 px from the side it comes from. With reduced motion it simply appears.</summary>
    private void Arrive(int step, int direction)
    {
        var panel = _panels[Math.Clamp(step, 0, _panels.Length - 1)];
        AnnounceStep(step);
        // Focus the first thing to act on: Download when it is offered, otherwise Next.
        DispatcherQueue.TryEnqueue(() =>
        {
            var first = step == FirstRunViewModel.StepModel && ViewModel.ShowOffer ? DownloadButton : NextButton;
            first.Focus(FocusState.Programmatic);
            // With large text the button can sit below the fold: scroll it into view once the step has laid out.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                first.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false }));
        });
        var move = (TranslateTransform)panel.RenderTransform;
        if (ReducedMotion)
        {
            panel.Opacity = 1;
            move.X = 0;
            return;
        }
        panel.Opacity = 0;
        move.X = 24 * direction;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(ArriveMs));
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = duration, EasingFunction = ease };
        var slide = new DoubleAnimation { From = 24 * direction, To = 0, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(fade, panel);
        Storyboard.SetTargetProperty(fade, "Opacity");
        Storyboard.SetTarget(slide, move);
        Storyboard.SetTargetProperty(slide, "X");
        var board = new Storyboard();
        board.Children.Add(fade);
        board.Children.Add(slide);
        board.Completed += (_, _) => { panel.Opacity = 1; move.X = 0; };
        board.Begin();
    }

    private async void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        // Only a change the person made counts; the switch also moves when the view model sets it.
        if (StartupToggle.IsOn != ViewModel.StartWithWindows)
            await ViewModel.SetStartWithWindowsAsync(StartupToggle.IsOn);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        ViewModel.Dispose();
        // The welcome counts as done however it was closed; the main window opens from this.
        _shell.CompleteFirstRun();
    }
}
