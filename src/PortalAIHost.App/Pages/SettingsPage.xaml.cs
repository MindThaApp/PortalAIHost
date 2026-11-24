using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using PortalAIHost.App.Services;
using PortalAIHost.App.ViewModels;

namespace PortalAIHost.App.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        ViewModel = new SettingsViewModel(AppServices.Shell, AppServices.Ui, AppServices.Strings, AppServices.Dialogs, AppServices.Startup, AppServices.Appearance);
        InitializeComponent();
        CrashWhatsSent.NavigateUri = new Uri(Links.PrivacyUrl);
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.MessageKind)) ShowSeverity();
            if (e.PropertyName == nameof(SettingsViewModel.PerformanceMessageKind)) ShowPerformanceSeverity();
        };
        ShowPerformanceSeverity();
        ShowSeverity();
    }

    public SettingsViewModel ViewModel { get; }

    private void ShowPerformanceSeverity() => PerformanceResultBar.Severity = ViewModel.PerformanceMessageKind switch
    {
        SettingsViewModel.MessageSuccess => InfoBarSeverity.Success,
        SettingsViewModel.MessageWarning => InfoBarSeverity.Warning,
        SettingsViewModel.MessageError => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Informational,
    };

    /// <summary>Brings the Performance section into view when a protocol link asked for it.</summary>
    public void ShowPendingSection()
    {
        var section = AppServices.PendingSettingsSection;
        AppServices.PendingSettingsSection = null;
        if (section == "performance")
        {
            DispatcherQueue.TryEnqueue(() => PerformanceHeader.StartBringIntoView());
        }
    }

    private void ShowSeverity() => ResultBar.Severity = ViewModel.MessageKind switch
    {
        SettingsViewModel.MessageSuccess => InfoBarSeverity.Success,
        SettingsViewModel.MessageWarning => InfoBarSeverity.Warning,
        SettingsViewModel.MessageError => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Informational,
    };

    // The folder applies when the box loses focus or Enter is pressed: checked first, then saved.
    private void OnFolderLostFocus(object sender, RoutedEventArgs e) => ViewModel.CommitFolderCommand.Execute(null);

    private void OnFolderKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        ViewModel.CommitFolderCommand.Execute(null);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        await ViewModel.LoadAsync();
        ShowPendingSection();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => ViewModel.Dispose();
}
