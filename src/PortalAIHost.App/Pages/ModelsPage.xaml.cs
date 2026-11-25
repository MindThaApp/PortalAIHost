using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PortalAIHost.App.Services;
using PortalAIHost.App.ViewModels;

namespace PortalAIHost.App.Pages;

public sealed partial class ModelsPage : Page
{
    public ModelsPage()
    {
        ViewModel = new ModelsViewModel(AppServices.Shell, AppServices.Ui, AppServices.Strings, AppServices.Dialogs, AppServices.Folders, picker: AppServices.FilePicker);
        Remote = new RemoteModelsViewModel(AppServices.Shell, AppServices.Ui, AppServices.Strings);
        InitializeComponent();
    }

    public ModelsViewModel ViewModel { get; }

    public RemoteModelsViewModel Remote { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        _ = Remote.LoadAsync();
        await ViewModel.LoadAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Remote.Dispose();
        ViewModel.Dispose();
    }
}
