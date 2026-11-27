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
        Budget = new MemoryBudgetViewModel(AppServices.Shell, AppServices.Ui, AppServices.Strings);
        InitializeComponent();
    }

    public ModelsViewModel ViewModel { get; }

    public RemoteModelsViewModel Remote { get; }

    public MemoryBudgetViewModel Budget { get; }

    public ClientLinkViewModel Link => AppServices.Shell.ClientLink;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel.ApplyCategoryLink(AppServices.PendingModelsCategory);
        AppServices.PendingModelsCategory = null;
        _ = Remote.LoadAsync();
        await ViewModel.LoadAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Remote.Dispose();
        Budget.Dispose();
        ViewModel.Dispose();
    }
}
