using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PortalAIHost.App.Pages;
using PortalAIHost.App.Services;
using PortalAIHost.App.ViewModels;

namespace PortalAIHost.App;

public sealed partial class MainWindow : Window
{
    private readonly ShellViewModel _shell = AppServices.Shell;
    private bool _firstRunShown;

    public MainWindow()
    {
        InitializeComponent();
        Title = AppServices.Strings.Get("Vm_AppTitle");
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        TitleText.Text = Title;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarArea);
        Root.ActualThemeChanged += (_, _) => ApplyTitleBarColours();
        Root.Loaded += (_, _) => ApplyTitleBarColours();
        ApplyTitleBarColours();
        Closed += (_, _) => _shell.Dispose();
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
        var dark = Root.ActualTheme == ElementTheme.Dark;
        var fg = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        var hover = dark ? Windows.UI.Color.FromArgb(0x20, 255, 255, 255) : Windows.UI.Color.FromArgb(0x18, 0, 0, 0);
        var press = dark ? Windows.UI.Color.FromArgb(0x38, 255, 255, 255) : Windows.UI.Color.FromArgb(0x30, 0, 0, 0);
        var inactive = dark ? Windows.UI.Color.FromArgb(0xFF, 0x9A, 0x9A, 0x9A) : Windows.UI.Color.FromArgb(0xFF, 0x70, 0x70, 0x70);
        var tb = AppWindow.TitleBar;
        tb.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        tb.ButtonForegroundColor = fg;
        tb.ButtonInactiveForegroundColor = inactive;
        tb.ButtonHoverBackgroundColor = hover;
        tb.ButtonHoverForegroundColor = fg;
        tb.ButtonPressedBackgroundColor = press;
        tb.ButtonPressedForegroundColor = fg;
    }

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
        _shell.ApprovalPresenter = AppServices.Dialogs.ShowApprovalAsync;
        Nav.SelectedItem = Nav.MenuItems[0];
        AppServices.NavigateTo = SelectPage;
        await _shell.InitializeAsync();
        AppServices.Ui.Post(ApplyFirstRun);
        AppServices.Ui.Post(() => OpenStartPage());
        await _shell.PumpApprovalsAsync();
    }

    private void SelectPage(string tag)
    {
        var item = Nav.MenuItems.Concat(Nav.FooterMenuItems).OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag);
        if (item is not null) Nav.SelectedItem = item;
        else Navigate(tag);
    }

    private void OpenStartPage()
    {
        var tag = AppOptions.StartPage;
        if (tag is null || _shell.ShowFirstRun) return;
        var item = Nav.MenuItems.Concat(Nav.FooterMenuItems).OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag);
        if (item is not null) Nav.SelectedItem = item;
    }

    private void ApplyFirstRun()
    {
        if (_shell.ShowFirstRun && !_firstRunShown)
        {
            _firstRunShown = true;
            Nav.IsPaneVisible = false;
            ContentFrame.Navigate(typeof(FirstRunPage));
        }
        else if (!_shell.ShowFirstRun && _firstRunShown)
        {
            _firstRunShown = false;
            Nav.IsPaneVisible = true;
            Nav.SelectedItem = Nav.MenuItems[0];
            ContentFrame.Navigate(typeof(HomePage));
        }
    }

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_shell.ShowFirstRun) return;
        if (args.SelectedItem is not NavigationViewItem item) return;
        Navigate((string)item.Tag);
    }

    /// <summary>Opens a page by name: home, models, apps, tryit or settings.</summary>
    public void Navigate(string tag)
    {
        var page = tag switch
        {
            "models" => typeof(ModelsPage),
            "apps" => typeof(AppsPage),
            "tryit" => typeof(TryItPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(HomePage),
        };
        if (ContentFrame.CurrentSourcePageType != page) ContentFrame.Navigate(page);
    }
}
