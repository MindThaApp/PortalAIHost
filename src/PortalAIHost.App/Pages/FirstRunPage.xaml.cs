using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PortalAIHost.App.Services;
using PortalAIHost.App.ViewModels;

namespace PortalAIHost.App.Pages;

public sealed partial class FirstRunPage : Page
{
    public FirstRunPage()
    {
        ViewModel = new FirstRunViewModel(AppServices.Shell, AppServices.Ui, AppServices.Strings);
        InitializeComponent();
    }

    public FirstRunViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e) => await ViewModel.LoadAsync();

    protected override void OnNavigatedFrom(NavigationEventArgs e) => ViewModel.Dispose();
}
