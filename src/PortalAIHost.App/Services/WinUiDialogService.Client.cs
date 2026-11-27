using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PortalAIHost.App.ViewModels;

namespace PortalAIHost.App.Services;

/// <summary>The two client-mode dialogs: the one-time "home PC unreachable" prompt and the pairing dialog.</summary>
public sealed partial class WinUiDialogService
{
    /// <summary>
    /// The one calm prompt. Returns true for "Use this laptop's model"; None, Escape or any other way out is false.
    /// </summary>
    public async Task<bool> ShowFallbackPromptAsync(string pcName)
    {
        if (Root is null) return false;
        await _one.WaitAsync();
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Root,
                RequestedTheme = ThemeOf(),
                Title = _s.Get("Vm_FallbackTitle"),
                Content = new TextBlock { Text = _s.Format("Vm_FallbackBody", pcName), TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = _s.Get("Vm_FallbackLocal"),
                SecondaryButtonText = _s.Get("Vm_FallbackNone"),
                DefaultButton = ContentDialogButton.Secondary,
            };
            AutomationProperties.SetName(dialog, _s.Get("Vm_FallbackTitle"));
            Current = dialog;
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally
        {
            Current = null;
            _one.Release();
        }
    }

    /// <summary>Shows the pairing dialog for <paramref name="vm"/>. Returns true when a PC was paired.</summary>
    public async Task<bool> ShowPairingAsync(PairingViewModel vm)
    {
        if (Root is null) return false;
        await _one.WaitAsync();
        try
        {
            var paired = false;
            var intro = new TextBlock { Text = _s.Get("Vm_PairIntro"), TextWrapping = TextWrapping.Wrap };
            var linkBox = new TextBox { PlaceholderText = _s.Get("Vm_PairLinkPlaceholder"), AcceptsReturn = false };
            AutomationProperties.SetName(linkBox, _s.Get("Vm_PairLinkName"));
            var paste = PasteButton(vm);
            var pcText = new TextBlock { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            var fingerprint = new TextBlock { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap };
            var nameBox = new TextBox { Header = _s.Get("Vm_PairDeviceName"), MaxLength = 64 };
            AutomationProperties.SetName(nameBox, _s.Get("Vm_PairDeviceName"));
            var progress = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            progress.Children.Add(new ProgressRing { IsActive = true, Width = 20, Height = 20 });
            progress.Children.Add(new TextBlock { Text = _s.Get("Vm_PairWorking"), VerticalAlignment = VerticalAlignment.Center });
            var error = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            };
            AutomationProperties.SetLiveSetting(error, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);

            var panel = new StackPanel { Spacing = 12, MinWidth = 380 };
            foreach (var child in new UIElement[] { intro, linkBox, paste, pcText, fingerprint, nameBox, progress, error })
                panel.Children.Add(child);

            var dialog = new ContentDialog
            {
                XamlRoot = Root,
                RequestedTheme = ThemeOf(),
                Title = _s.Get("Vm_PairTitle"),
                Content = panel,
                PrimaryButtonText = _s.Get("Vm_PairNext"),
                CloseButtonText = _s.Get("Vm_PairCancel"),
                DefaultButton = ContentDialogButton.Primary,
            };
            AutomationProperties.SetName(dialog, _s.Get("Vm_PairTitle"));

            void Refresh()
            {
                intro.Visibility = linkBox.Visibility = paste.Visibility = vm.OnLink ? Visibility.Visible : Visibility.Collapsed;
                pcText.Visibility = fingerprint.Visibility = nameBox.Visibility = vm.OnConfirm ? Visibility.Visible : Visibility.Collapsed;
                progress.Visibility = vm.OnWorking ? Visibility.Visible : Visibility.Collapsed;
                pcText.Text = _s.Format("Vm_PairConfirmPc", vm.PcName);
                fingerprint.Text = _s.Format("Vm_PairFingerprint", vm.FingerprintText);
                error.Text = vm.Error;
                error.Visibility = vm.HasError ? Visibility.Visible : Visibility.Collapsed;
                dialog.PrimaryButtonText = vm.OnConfirm ? _s.Get("Vm_PairConfirm") : _s.Get("Vm_PairNext");
                dialog.IsPrimaryButtonEnabled = vm.OnLink ? vm.CanCheck : vm.CanConfirm;
                dialog.SecondaryButtonText = vm.OnConfirm ? _s.Get("Vm_PairBack") : "";
                paste.Style = vm.ClipboardHasLink ? (Style)Application.Current.Resources["AccentButtonStyle"] : null;
            }

            vm.PropertyChanged += (_, _) => Refresh();
            linkBox.TextChanged += (_, _) => { if (vm.LinkText != linkBox.Text) vm.LinkText = linkBox.Text; };
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PairingViewModel.LinkText) && linkBox.Text != vm.LinkText) linkBox.Text = vm.LinkText; };
            nameBox.Text = vm.DeviceName;
            nameBox.TextChanged += (_, _) => { if (vm.DeviceName != nameBox.Text) vm.DeviceName = nameBox.Text; };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                args.Cancel = true; // The dialog stays open: the next step, progress or an error shows inside it.
                if (vm.OnLink) vm.CheckCommand.Execute(null);
                else if (vm.OnConfirm) vm.ConfirmCommand.Execute(null);
            };
            dialog.SecondaryButtonClick += (_, args) =>
            {
                args.Cancel = true;
                vm.BackCommand.Execute(null);
            };
            dialog.CloseButtonClick += (_, _) => vm.CancelCommand.Execute(null);
            vm.Paired += _ => { paired = true; dialog.Hide(); };

            await ReadClipboardAsync(vm);
            Refresh();
            Current = dialog;
            await dialog.ShowAsync();
            return paired;
        }
        finally
        {
            Current = null;
            _one.Release();
        }
    }

    private Button PasteButton(PairingViewModel vm)
    {
        var button = new Button { HorizontalAlignment = HorizontalAlignment.Left };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new FontIcon { Glyph = "", FontSize = 16 });
        content.Children.Add(new TextBlock { Text = _s.Get("Vm_PairPaste") });
        button.Content = content;
        AutomationProperties.SetName(button, _s.Get("Vm_PairPaste"));
        AutomationProperties.SetHelpText(button, _s.Get("Vm_PairPasteTip"));
        ToolTipService.SetToolTip(button, _s.Get("Vm_PairPasteTip"));
        button.Click += async (_, _) =>
        {
            var text = await ClipboardTextAsync();
            if (text is not null) vm.UseLink(text);
        };
        return button;
    }

    private static async Task ReadClipboardAsync(PairingViewModel vm) => vm.NoteClipboard(await ClipboardTextAsync());

    /// <summary>Reads the clipboard text only when the user asks (the Paste button) or to highlight that button. Never throws.</summary>
    private static async Task<string?> ClipboardTextAsync()
    {
        try
        {
            var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            return content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)
                ? await content.GetTextAsync()
                : null;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
