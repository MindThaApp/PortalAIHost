using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PortalAIHost.App.Services;
using PortalAIHost.App.ViewModels;

namespace PortalAIHost.App.Pages;

public sealed partial class HomePage : Page
{
    public HomePage()
    {
        ViewModel = new HomeViewModel(AppServices.Shell, AppServices.Ui, AppServices.Strings);
        ViewModel.Health.Navigate = tag => Go(tag, typeof(SettingsPage));
        InitializeComponent();
    }

    public HomeViewModel ViewModel { get; }

    private void Go(string tag, System.Type page)
    {
        if (AppServices.NavigateTo is { } go) go(tag);
        else Frame.Navigate(page);
    }

    private void OnSeeModels(object sender, RoutedEventArgs e) => Go("models", typeof(ModelsPage));

    private void OnSeeApps(object sender, RoutedEventArgs e) => Go("apps", typeof(AppsPage));

    private void OnTryIt(object sender, RoutedEventArgs e) => Go("tryit", typeof(TryItPage));

    private void OnOpenSettings(object sender, RoutedEventArgs e) => Go("settings", typeof(SettingsPage));

    private void OnDeviceNoticeClosed(InfoBar sender, object args) => ViewModel.Shell.DeviceNotice = "";

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        AppServices.MainVisibilityChanged += OnWindowVisible;
        ViewModel.Live.Start();
        await ViewModel.LoadAsync();
    }

    private void OnWindowVisible(bool visible)
    {
        if (visible) ViewModel.Live.Start();
        else ViewModel.Live.Stop();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        AppServices.MainVisibilityChanged -= OnWindowVisible;
        ViewModel.Dispose();
    }
}
