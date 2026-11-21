using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PortalAIHost.App.Services;
using PortalAIHost.App.ViewModels;

namespace PortalAIHost.App.Pages;

public sealed partial class WhatsNewPage : Page
{
    public WhatsNewPage()
    {
        ViewModel = new WhatsNewViewModel(AppServices.Strings);
        InitializeComponent();
        // Esc closes the page, like a dialog.
        var esc = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        esc.Invoked += (_, e) => { e.Handled = true; Go("home"); };
        KeyboardAccelerators.Add(esc);
    }

    public WhatsNewViewModel ViewModel { get; }

    private static void Go(string tag) => AppServices.NavigateTo?.Invoke(tag);

    private void OnShowMe(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag }) Go(tag);
    }

    private void OnDone(object sender, RoutedEventArgs e) => Go("home");
}
