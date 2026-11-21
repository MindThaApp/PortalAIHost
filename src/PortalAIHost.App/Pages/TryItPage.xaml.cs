using Microsoft.UI.Xaml.Controls;
using PortalAIHost.App.Services;
using PortalAIHost.App.ViewModels;

namespace PortalAIHost.App.Pages;

public sealed partial class TryItPage : Page
{
    public TryItPage()
    {
        ViewModel = new TryItViewModel(AppServices.Shell, AppServices.Ui, AppServices.Strings);
        InitializeComponent();
    }

    public TryItViewModel ViewModel { get; }
}
