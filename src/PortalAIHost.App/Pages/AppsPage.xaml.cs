using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PortalAIHost.App.Services;
using PortalAIHost.App.ViewModels;

namespace PortalAIHost.App.Pages;

public sealed partial class AppsPage : Page
{
    public AppsPage()
    {
        ViewModel = new AppsViewModel(AppServices.Shell, AppServices.Ui, AppServices.Strings, AppServices.Dialogs);
        InitializeComponent();
    }

    public AppsViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e) => await ViewModel.LoadAsync();
}
