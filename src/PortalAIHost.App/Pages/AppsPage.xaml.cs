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

    // Leaving the page makes a removal that is still waiting final, so a removed app never keeps working.
    protected override async void OnNavigatedFrom(NavigationEventArgs e)
    {
        await ViewModel.FlushAsync();
        ViewModel.Dispose();
    }

    private void OnUndoKey(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewModel.UndoCommand.Execute(null);
    }
}
