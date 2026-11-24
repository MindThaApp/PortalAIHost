using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PortalAIHost.App.Services;
using PortalAIHost.App.ViewModels;

namespace PortalAIHost.App.Pages;

public sealed partial class TryItPage : Page
{
    public TryItPage()
    {
        ViewModel = new TryItViewModel(AppServices.Shell, AppServices.Ui, AppServices.Strings);
        InitializeComponent();
        ViewModel.PropertyChanged += (_, e) =>
        {
            // Keep the newest words in view while an answer streams in.
            if (e.PropertyName == nameof(TryItViewModel.Reply) && ViewModel.IsBusy)
                Scroller.ChangeView(null, Scroller.ScrollableHeight, null, true);
        };
    }

    public TryItViewModel ViewModel { get; }

    private void OnSuggestion(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string text }) ViewModel.UseSuggestionCommand.Execute(text);
    }

    private void OnSendAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (ViewModel.SendCommand.CanExecute(null)) ViewModel.SendCommand.Execute(null);
    }
}
