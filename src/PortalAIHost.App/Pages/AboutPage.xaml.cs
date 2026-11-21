using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PortalAIHost.App.Services;
using PortalAIHost.App.ViewModels;

namespace PortalAIHost.App.Pages;

public sealed partial class AboutPage : Page
{
    public AboutPage()
    {
        ViewModel = new AboutViewModel(AppServices.Strings, CopyText);
        InitializeComponent();
        // The addresses live in Links only.
        FeedbackLink.NavigateUri = new Uri(Links.FeedbackUrl);
        PrivacyLink.NavigateUri = new Uri(Links.PrivacyUrl);
        LlamaLink.NavigateUri = new Uri(Links.LlamaCppUrl);
        DataFolderLink.Visibility = Directory.Exists(DataFolder) ? Visibility.Visible : Visibility.Collapsed;
    }

    public AboutViewModel ViewModel { get; }

    private static string DataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PortalAIHost");

    private static void CopyText(string text)
    {
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The clipboard can be busy; the version is also selectable on the page.
        }
    }

    private void OnWhatsNew(object sender, RoutedEventArgs e) => AppServices.NavigateTo?.Invoke("whatsnew");

    /// <summary>Opens the folder in Explorer. Nothing is created, changed or removed.</summary>
    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(DataFolder)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{DataFolder}\"") { UseShellExecute = true });
    }
}
