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
        ViewModel = new HomeViewModel(AppServices.Shell, AppServices.Strings);
        InitializeComponent();
    }

    public HomeViewModel ViewModel { get; }

    private void OnSeeModels(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(ModelsPage));

    protected override void OnNavigatedFrom(NavigationEventArgs e) => ViewModel.Dispose();
}
