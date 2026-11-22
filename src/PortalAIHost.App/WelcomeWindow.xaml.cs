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
    private const int DesignHeight = 520;
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
        UpdateDots();
        Root.Loaded += async (_, _) =>
        {
            AutomationProperties_SetDots();
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

    private void ApplyTextScale(double k)
    {
        BodyScroll.FontSize = 14 * k;
        SkipButton.FontSize = 14 * k;
        BackButton.FontSize = 14 * k;
        NextButton.FontSize = 14 * k;
        HeroTitle.FontSize = 20 * Math.Min(k, 1.5);
        StepText.FontSize = 13 * k;
        SizeLine.FontSize = 13 * k;
        Head0.FontSize = 20 * k;
        Head1.FontSize = 20 * k;
        Head2.FontSize = 20 * k;
    }

    private void AddKeys()
    {
        // Esc means Skip for now; Enter means Next, unless a button or switch has the focus and takes Enter itself.
        var esc = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        esc.Invoked += (_, e) => { e.Handled = true; ViewModel.SkipCommand.Execute(null); };
        Root.KeyboardAccelerators.Add(esc);
        var enter = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Enter };
        enter.Invoked += (_, e) =>
        {
            if (Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Root.XamlRoot) is Button or ToggleSwitch or HyperlinkButton) return;
            e.Handled = true;
            ViewModel.NextCommand.Execute(null);
        };
        Root.KeyboardAccelerators.Add(enter);
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
                Arrive(ViewModel.Step, ViewModel.Step >= _lastStep ? 1 : -1);
                _lastStep = ViewModel.Step;
                break;
        }
    }

    private void UpdateDots()
    {
        Ellipse[] dots = [Dot0, Dot1, Dot2];
        for (var i = 0; i < dots.Length; i++) dots[i].Visibility = i == ViewModel.Step ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The new step fades in while sliding 24 px from the side it comes from. With reduced motion it simply appears.</summary>
    private void Arrive(int step, int direction)
    {
        var panel = _panels[Math.Clamp(step, 0, _panels.Length - 1)];
        // Focus the first thing to act on: Download when it is offered, otherwise Next.
        DispatcherQueue.TryEnqueue(() => (step == FirstRunViewModel.StepModel && ViewModel.ShowOffer ? DownloadButton : NextButton).Focus(FocusState.Programmatic));
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
