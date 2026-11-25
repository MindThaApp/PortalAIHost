using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PortalAIHost.App.Services;
using PortalAIHost.App.ViewModels;

namespace PortalAIHost.App.Pages;

public sealed partial class DevicesPage : Page
{
    public DevicesPage()
    {
        ViewModel = new DevicesViewModel(AppServices.Shell, AppServices.Ui, AppServices.Strings, AppServices.Dialogs);
        InitializeComponent();
    }

    public DevicesViewModel ViewModel { get; }

    // Reading the host happens off the UI thread; leaving the page cancels whatever is still being read.
    protected override void OnNavigatedTo(NavigationEventArgs e) => _ = Task.Run(ViewModel.LoadAsync);

    protected override void OnNavigatedFrom(NavigationEventArgs e) => ViewModel.Dispose();
}
