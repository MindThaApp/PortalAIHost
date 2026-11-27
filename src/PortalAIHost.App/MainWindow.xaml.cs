using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PortalAIHost.App.Pages;
using PortalAIHost.App.Services;
using PortalAIHost.App.ViewModels;

namespace PortalAIHost.App;

public sealed partial class MainWindow : Window, IAppearanceApplier
{
    private readonly ShellViewModel _shell = AppServices.Shell;
    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();
    private readonly Windows.UI.ViewManagement.AccessibilitySettings _access = new();
    private bool _firstRunShown;
    private AppearancePrefs _prefs = AppearancePrefs.Default;

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "portalaihost.ico"));
        Title = AppServices.Strings.Get("Vm_AppTitle");
        TitleText.Text = Title;
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(AppMenuButton, AppServices.Strings.Get("Vm_AppMenuTip"));
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarArea);
        Root.ActualThemeChanged += (_, _) => ApplyTitleBarColours();
        Root.Loaded += (_, _) => { ApplyTitleBarColours(); UpdateCaptionRegions(); };
        Root.SizeChanged += (_, _) => UpdateCaptionRegions();
        AppMenuButton.SizeChanged += (_, _) => UpdateCaptionRegions();
        AppServices.Appearance = this;
        VisibilityChanged += (_, e) => AppServices.MainVisibilityChanged?.Invoke(e.Visible);
        Apply(AppOptions.WithOverrides(_shell.State.Appearance));
        // Windows high contrast can change while the window is open: switch materials and caption colours with it.
        // (AccessibilitySettings.HighContrastChanged cannot be subscribed to in an unpackaged run; UISettings can.)
        try { _uiSettings.ColorValuesChanged += (_, _) => AppServices.Ui.Post(() => Apply(_prefs)); }
        catch (System.Runtime.InteropServices.COMException) { }
        AddShortcuts();
        Closed += (_, _) =>
        {
            // A removal of app access still waiting out its undo time is sent now, so it is never lost.
            try { Task.Run(_shell.FlushAsync).Wait(TimeSpan.FromSeconds(3)); }
            catch (AggregateException) { }
            _shell.Dispose();
        };
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 760));
        if (AppOptions.OffScreen)
            AppWindow.Move(new Windows.Graphics.PointInt32(-3000, 100));
        if (AppOptions.ShotDir is { Length: > 0 } shotDir) StartShotWatcher(shotDir);
        _shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.ShowFirstRun)) ApplyFirstRun();
        };
    }

    /// <summary>Makes the caption buttons match the app theme (and follow it when it changes).</summary>
    private void ApplyTitleBarColours()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        var tb = AppWindow.TitleBar;
        if (_access.HighContrast)
        {
            // High contrast: leave the caption buttons to the system colours.
            tb.ButtonBackgroundColor = null;
            tb.ButtonInactiveBackgroundColor = null;
            tb.ButtonForegroundColor = null;
            tb.ButtonInactiveForegroundColor = null;
            tb.ButtonHoverBackgroundColor = null;
            tb.ButtonHoverForegroundColor = null;
            tb.ButtonPressedBackgroundColor = null;
            tb.ButtonPressedForegroundColor = null;
            return;
        }
        var dark = Root.ActualTheme == ElementTheme.Dark;
        var fg = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        var hover = dark ? Windows.UI.Color.FromArgb(0x20, 255, 255, 255) : Windows.UI.Color.FromArgb(0x18, 0, 0, 0);
        var press = dark ? Windows.UI.Color.FromArgb(0x38, 255, 255, 255) : Windows.UI.Color.FromArgb(0x30, 0, 0, 0);
        var inactive = dark ? Windows.UI.Color.FromArgb(0xFF, 0x9A, 0x9A, 0x9A) : Windows.UI.Color.FromArgb(0xFF, 0x70, 0x70, 0x70);
        tb.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        tb.ButtonForegroundColor = fg;
        tb.ButtonInactiveForegroundColor = inactive;
        tb.ButtonHoverBackgroundColor = hover;
        tb.ButtonHoverForegroundColor = fg;
        tb.ButtonPressedBackgroundColor = press;
        tb.ButtonPressedForegroundColor = fg;
    }

    /// <summary>Applies theme, material, corners, density, text size and motion to the window. Called live from Settings.</summary>
    public void Apply(AppearancePrefs prefs)
    {
        _prefs = prefs;
        Root.RequestedTheme = prefs.Theme switch
        {
            ThemeChoice.Light => ElementTheme.Light,
            ThemeChoice.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        // High contrast and the solid choice use a plain system background, never a see-through material.
        var plain = _access.HighContrast || prefs.Material == MaterialChoice.Solid;
        SystemBackdrop = plain ? null : prefs.Material == MaterialChoice.Acrylic
            ? new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop()
            : new Microsoft.UI.Xaml.Media.MicaBackdrop();
        Root.Background = plain ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"] : null;
        AppearanceMetrics.Instance.Set(prefs);
        ContentFrame.FontSize = AppearanceMath.BodyFontSize(prefs.TextStep);
        ApplyTitleBarColours();
    }

    private bool ReducedMotion => AppearanceMath.ReducedMotion(_prefs.Motion, _uiSettings.AnimationsEnabled);

    /// <summary>The app-menu button must stay clickable inside the draggable title strip.</summary>
    private void UpdateCaptionRegions()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported() || AppMenuButton.XamlRoot is null) return;
        var scale = AppMenuButton.XamlRoot.RasterizationScale;
        var p = AppMenuButton.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
        var rect = new Windows.Graphics.RectInt32((int)(p.X * scale), (int)(p.Y * scale),
            (int)(AppMenuButton.ActualWidth * scale), (int)(AppMenuButton.ActualHeight * scale));
        Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(AppWindow.Id)
            .SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough, [rect]);
    }

    /// <summary>Ctrl+1 to Ctrl+4 pick a page, Ctrl+comma opens Settings, F1 opens About.</summary>
    private void AddShortcuts()
    {
        void Add(Windows.System.VirtualKey key, Windows.System.VirtualKeyModifiers mods, string page)
        {
            var a = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = key, Modifiers = mods };
            a.Invoked += (_, e) => { e.Handled = true; if (!_shell.ShowFirstRun) SelectPage(page); };
            Root.KeyboardAccelerators.Add(a);
        }
        var ctrl = Windows.System.VirtualKeyModifiers.Control;
        Add(Windows.System.VirtualKey.Number1, ctrl, "home");
        Add(Windows.System.VirtualKey.Number2, ctrl, "models");
        Add(Windows.System.VirtualKey.Number3, ctrl, "apps");
        Add(Windows.System.VirtualKey.Number4, ctrl, "tryit");
        Add((Windows.System.VirtualKey)188, ctrl, "settings"); // comma
        Add(Windows.System.VirtualKey.F1, Windows.System.VirtualKeyModifiers.None, "about");
    }

    private void OnMenuPage(object sender, RoutedEventArgs e)
    {
        if (!_shell.ShowFirstRun && sender is FrameworkElement { Tag: string tag }) SelectPage(tag);
    }

    private async void OnMenuShortcuts(object sender, RoutedEventArgs e) =>
        await AppServices.Dialogs.ShowInfoAsync(AppServices.Strings.Get("Vm_ShortcutsTitle"), AppServices.Strings.Get("Vm_ShortcutsBody"), AppServices.Strings.Get("Vm_Close"));

    /// <summary>
    /// Check support: saves a picture of the window (or the open dialog) whenever a new take-NAME.txt appears in the folder.
    /// It only writes NAME.png there and never deletes or changes anything.
    /// </summary>
    private void StartShotWatcher(string dir)
    {
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(400);
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "take-*.txt"))
                {
                    if (!done.Add(f)) continue;
                    var name = Path.GetFileNameWithoutExtension(f)["take-".Length..];
                    var target0 = Path.Combine(dir, name + ".png");
                    if (File.Exists(target0)) continue; // never overwrite an existing file
                    FrameworkElement target = AppServices.Dialogs.Current ?? (FrameworkElement)Root;
                    var rtb = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
                    await rtb.RenderAsync(target);
                    var pixels = await rtb.GetPixelsAsync();
                    using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                    var enc = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
                    enc.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                        (uint)rtb.PixelWidth, (uint)rtb.PixelHeight, 96, 96, System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(pixels));
                    await enc.FlushAsync();
                    var bytes = new byte[stream.Size];
                    stream.Seek(0);
                    await stream.ReadAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes), (uint)bytes.Length, Windows.Storage.Streams.InputStreamOptions.None);
                    await File.WriteAllBytesAsync(target0, bytes);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
            }
            timer.Start();
        };
        Closed += (_, _) => timer.Stop();
        timer.Start();
    }

    /// <summary>Connects to the host (starting it if needed) and shows the first page.</summary>
    public async Task StartAsync()
    {
        while (Root.XamlRoot is null) await Task.Delay(20);
        AppServices.Dialogs.Root = Root.XamlRoot;
        AppServices.FilePicker.WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _shell.ApprovalPresenter = AppServices.Dialogs.ShowApprovalAsync;
        _shell.ClientLink.FallbackPromptRequested += () => _ = ClientFlows.ShowFallbackPromptAsync();
        Nav.SelectedItem = Nav.MenuItems[0];
        AppServices.NavigateTo = SelectPage;
        await _shell.InitializeAsync();
        AppServices.Ui.Post(ApplyFirstRun);
        AppServices.Ui.Post(() => OpenStartPage());
        AppServices.Ui.Post(() =>
        {
            if (!_shell.PairOnStart || _shell.ShowFirstRun) return;
            _shell.PairOnStart = false;
            _ = ClientFlows.PairHomePcAsync();
        });
        await _shell.PumpApprovalsAsync();
    }

    private void SelectPage(string fullTag)
    {
        var (tag, section) = PageArgs.Split(fullTag);
        AppServices.PendingSettingsSection = tag == "settings" ? section : null;
        var item = Nav.MenuItems.Concat(Nav.FooterMenuItems).OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag);
        if (item is not null) Nav.SelectedItem = item;
        else
        {
            Nav.SelectedItem = null; // pages outside the menu (What's new) leave no menu row selected
            Navigate(tag);
        }

        // Already on Settings: the page does not reload, so ask it to scroll to the section.
        if (ContentFrame.Content is SettingsPage settings) settings.ShowPendingSection();
    }

    private void OpenStartPage()
    {
        if (_shell.ShowFirstRun) return;
        var tag = AppOptions.StartPage;
        if (tag is null)
        {
            // One screen at launch: What's new opens once after an update, never together with the welcome.
            if (_shell.State.LastSeenVersion == Links.WhatsNewVersion) return;
            _shell.State.MarkVersionSeen(Links.WhatsNewVersion);
            tag = "whatsnew";
        }
        SelectPage(tag);
    }

    private void ApplyFirstRun()
    {
        if (_shell.ShowFirstRun && !_firstRunShown)
        {
            // The welcome is its own window; the main window stays out of sight until it closes.
            _firstRunShown = true;
            HideForWelcome();
            WindowShow.Open(new WelcomeWindow());
        }
        else if (!_shell.ShowFirstRun && _firstRunShown)
        {
            _firstRunShown = false;
            _shell.State.MarkVersionSeen(Links.WhatsNewVersion);
            Nav.SelectedItem = Nav.MenuItems[0];
            ContentFrame.Navigate(typeof(HomePage));
            ShowAgain();
        }
        else if (!_shell.ShowFirstRun)
        {
            ShowAgain();
        }
    }

    private bool _hidden;

    /// <summary>Hides the main window while the welcome is open (also used at launch on a new install, so it never flashes).</summary>
    public void HideForWelcome()
    {
        _hidden = true;
        AppWindow.Hide();
    }

    private void ShowAgain()
    {
        if (!_hidden) return;
        _hidden = false;
        if (AppOptions.NoActivate) AppWindow.Show(false);
        else
        {
            AppWindow.Show();
            Activate();
        }
    }

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_shell.ShowFirstRun) return;
        if (args.SelectedItem is not NavigationViewItem item) return;
        Navigate((string)item.Tag);
    }

    /// <summary>Opens a page by name: home, models, apps, tryit, settings, about or whatsnew.</summary>
    public void Navigate(string tag)
    {
        var page = tag switch
        {
            "models" => typeof(ModelsPage),
            "apps" => typeof(AppsPage),
            "devices" => typeof(DevicesPage),
            "tryit" => typeof(TryItPage),
            "settings" => typeof(SettingsPage),
            "about" => typeof(AboutPage),
            "whatsnew" => typeof(WhatsNewPage),
            _ => typeof(HomePage),
        };
        if (ContentFrame.CurrentSourcePageType == page) return;
        // Slides are skipped when Windows animations are off or the user chose reduced motion.
        Microsoft.UI.Xaml.Media.Animation.NavigationTransitionInfo info = ReducedMotion
            ? new Microsoft.UI.Xaml.Media.Animation.SuppressNavigationTransitionInfo()
            : new Microsoft.UI.Xaml.Media.Animation.EntranceNavigationTransitionInfo();
        ContentFrame.Navigate(page, null, info);
    }
}
