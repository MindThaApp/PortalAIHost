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
        Closed += (_, _) => _shell.Dispose();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 760));
        if (AppOptions.OffScreen)
            AppWindow.Move(new Windows.Graphics.PointInt32(-3000, 100));
        _shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.ShowFirstRun)) ApplyFirstRun();
        };
    }

    /// <summary>Connects to the host (starting it if needed) and shows the first page.</summary>
    public async Task StartAsync()
    {
        while (Root.XamlRoot is null) await Task.Delay(20);
        AppServices.Dialogs.Root = Root.XamlRoot;
        _shell.ApprovalPresenter = AppServices.Dialogs.ShowApprovalAsync;
        Nav.SelectedItem = Nav.MenuItems[0];
        await _shell.InitializeAsync();
        AppServices.Ui.Post(ApplyFirstRun);
        AppServices.Ui.Post(() => OpenStartPage());
        await _shell.PumpApprovalsAsync();
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
