using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using PortalAIHost.App.Services;
using Windows.Globalization;

namespace PortalAIHost.App.Controls;

/// <summary>
/// Language choice: "Windows default" or one of the shipped languages. The choice is stored and
/// applied through ApplicationLanguages.PrimaryLanguageOverride; it takes effect after a restart.
/// </summary>
public sealed partial class LanguagePicker : UserControl
{
    /// <summary>Set by the app to remember the choice in its own settings file.</summary>
    public static Action<string>? Persist { get; set; }

    private readonly string _startTag;
    private bool _loading = true;

    public LanguagePicker()
    {
        InitializeComponent();
        _startTag = CurrentOverride();
        var loader = new ResourceLoader();
        var options = LanguageCatalog.Options(Text(loader, "LanguageWindowsDefault", "Windows default"), Text(loader, "LanguageWindowsDefaultHint", "Follows Windows"));
        Picker.ItemsSource = options;
        var index = 0;
        for (var i = 0; i < options.Count; i++)
            if (string.Equals(options[i].Tag, _startTag, StringComparison.OrdinalIgnoreCase)) index = i;
        Picker.SelectedIndex = index;
        _loading = false;
    }

    /// <summary>Applies the stored choice at startup, before any page loads its strings.</summary>
    public static void ApplyStored(string? stored)
    {
        try
        {
            var tag = LanguageCatalog.Normalize(stored);
            if (!string.Equals(ApplicationLanguages.PrimaryLanguageOverride ?? "", tag, StringComparison.OrdinalIgnoreCase))
                ApplicationLanguages.PrimaryLanguageOverride = tag;
        }
        catch (Exception)
        {
            // Unpackaged runs have no language override; Windows' own choice stays.
        }
    }

    private static string CurrentOverride()
    {
        try { return LanguageCatalog.Normalize(ApplicationLanguages.PrimaryLanguageOverride); }
        catch (Exception) { return ""; }
    }

    private static string Text(ResourceLoader loader, string key, string fallback)
    {
        try { var s = loader.GetString(key); return string.IsNullOrEmpty(s) ? fallback : s; }
        catch (Exception) { return fallback; }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Picker.SelectedItem is not LanguageOption option) return;
        ApplyStored(option.Tag);
        Persist?.Invoke(option.Tag);
        RestartRow.Visibility = string.Equals(option.Tag, _startTag, StringComparison.OrdinalIgnoreCase) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnRestart(object sender, RoutedEventArgs e)
    {
        try { Microsoft.Windows.AppLifecycle.AppInstance.Restart(""); }
        catch (Exception) { }
    }
}
