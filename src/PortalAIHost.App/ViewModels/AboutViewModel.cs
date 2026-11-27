using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PortalAIHost.App.Services;

namespace PortalAIHost.App.ViewModels;

/// <summary>One highlight card on the What's new page.</summary>
public sealed record WhatsNewCard(string Title, string Body, string ShowMeText, string Page, string ShowMeName)
{
    /// <summary>Thank-you and fix cards have no page to show.</summary>
    public bool HasLink => Page.Length > 0;
}

/// <summary>About page: version, licence state in words, licences, and where to send feedback.</summary>
public sealed partial class AboutViewModel : ObservableObject
{
    private readonly IStrings _s;
    private readonly Action<string> _copy;

    public AboutViewModel(IStrings strings, Action<string> copyToClipboard)
    {
        _s = strings;
        _copy = copyToClipboard;
        VersionLine = _s.Format("Vm_AboutVersion", Links.AppVersion);
        LicenceState = _s.Get("Vm_AboutLicenceState");
        LlamaLicence = _s.Get("Vm_LlamaLicence");
        ThirdParty = _s.Get("Vm_ThirdParty");
    }

    public string VersionLine { get; }
    public string LicenceState { get; }
    public string LlamaLicence { get; }
    public string ThirdParty { get; }

    [ObservableProperty] private string _copyNote = "";

    /// <summary>Puts the version text on the clipboard so it can be pasted into a message to support.</summary>
    [RelayCommand]
    private void CopyVersion()
    {
        _copy(_s.Format("Vm_AboutCopyText", Links.AppVersion));
        CopyNote = _s.Get("Vm_AboutCopied");
    }
}

/// <summary>What's new for this release: a few highlight cards, each with a link to its page.</summary>
public sealed class WhatsNewViewModel
{
    public WhatsNewViewModel(IStrings s)
    {
        Heading = s.Format("Vm_WhatsNewHeading", Links.WhatsNewVersion);
        Cards = new[]
        {
            Card(s, "Vm_Wn1", ""),
            Card(s, "Vm_Wn4", ""),
            Card(s, "Vm_Wn5", "settings"),
            Card(s, "Vm_Wn6", ""),
            Card(s, "Vm_Wn7", ""),
            Card(s, "Vm_Wn8", "settings"),
            Card(s, "Vm_Wn9", "devices"),
            Card(s, "Vm_Wn10", "models"),
            Card(s, "Vm_Wn11", ""),
            Card(s, "Vm_Wn12", "devices"),
            Card(s, "Vm_Wn14", "models"),
        };
    }

    private static WhatsNewCard Card(IStrings s, string key, string page) =>
        new(s.Get(key + "Title"), s.Get(key + "Body"), s.Get("Vm_WnShowMe"), page, s.Format("Vm_WnShowMeName", s.Get(key + "Title")));

    public string Heading { get; }
    public IReadOnlyList<WhatsNewCard> Cards { get; }
}
