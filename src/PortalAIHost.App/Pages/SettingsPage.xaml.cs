using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Portal.AiHost.Core.Remote;
using PortalAIHost.App.Services;
using PortalAIHost.App.ViewModels;

namespace PortalAIHost.App.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        ViewModel = new SettingsViewModel(AppServices.Shell, AppServices.Ui, AppServices.Strings, AppServices.Dialogs, AppServices.Startup, AppServices.Appearance);
        AppModels = new AppModelsViewModel(AppServices.Shell, AppServices.Ui, AppServices.Strings);
        InitializeComponent();
        CrashWhatsSent.NavigateUri = new Uri(Links.PrivacyUrl);
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.MessageKind)) ShowSeverity();
            if (e.PropertyName == nameof(SettingsViewModel.PerformanceMessageKind)) ShowPerformanceSeverity();
            if (e.PropertyName == nameof(SettingsViewModel.PairingQrText)) ShowQr();
        };
        ShowPerformanceSeverity();
        ViewModel.PairRequested += () => _ = ClientFlows.PairHomePcAsync();
        ShowSeverity();
    }

    public SettingsViewModel ViewModel { get; }

    public AppModelsViewModel AppModels { get; }

    /// <summary>Copies the pairing link (the text of the QR code). Never throws: a busy clipboard is retried, then ignored.</summary>
    private void OnCopyPairingLink(object sender, RoutedEventArgs e)
    {
        var text = ViewModel.PairingLinkText;
        if (text.Length == 0) return;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText(text);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                return;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                System.Threading.Thread.Sleep(50);
            }
        }
    }

    private void OnModelControlToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch { Tag: string id } toggle) _ = ViewModel.SetModelControlAsync(id, toggle.IsOn);
    }

    private void OnTakeBack(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string kind }) ViewModel.TakeBackCommand.Execute(kind);
    }

    private void OnRemoveDevice(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id }) ViewModel.RemoveDeviceCommand.Execute(id);
    }

    /// <summary>Draws the pairing QR code: black modules on white with the 4-module quiet zone, 6 pixels per module.</summary>
    private void ShowQr()
    {
        var text = ViewModel.PairingQrText;
        if (text.Length == 0)
        {
            PairingQr.Source = null;
            return;
        }

        try
        {
            var qr = QrCode.Encode(text);
            const int scale = 6, quiet = 4;
            var side = (qr.Size + 2 * quiet) * scale;
            var pixels = new byte[side * side * 4];
            Array.Fill(pixels, (byte)0xFF);
            for (var y = 0; y < qr.Size; y++)
            {
                for (var x = 0; x < qr.Size; x++)
                {
                    if (!qr[x, y]) continue;
                    for (var dy = 0; dy < scale; dy++)
                    {
                        for (var dx = 0; dx < scale; dx++)
                        {
                            var i = (((y + quiet) * scale + dy) * side + (x + quiet) * scale + dx) * 4;
                            pixels[i] = pixels[i + 1] = pixels[i + 2] = 0; // blue, green, red; alpha stays opaque
                        }
                    }
                }
            }

            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap(side, side);
            using (var stream = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsStream(bitmap.PixelBuffer))
            {
                stream.Write(pixels, 0, pixels.Length);
            }

            bitmap.Invalidate();
            PairingQr.Source = bitmap;
        }
        catch (ArgumentException)
        {
            PairingQr.Source = null; // too long for the code: the digits above still work
        }
    }

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
        _ = AppModels.LoadAsync();
        await ViewModel.LoadAsync();
        ShowPendingSection();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        AppModels.Dispose();
        ViewModel.Dispose();
    }
}
