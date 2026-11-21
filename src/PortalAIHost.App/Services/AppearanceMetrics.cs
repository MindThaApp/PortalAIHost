using System.ComponentModel;
using Microsoft.UI.Xaml;

namespace PortalAIHost.App.Services;

/// <summary>
/// Card corner radius and padding as live values. The card style binds to them, so a change in Settings
/// reaches every card on screen without reopening the page.
/// </summary>
[Microsoft.UI.Xaml.Data.Bindable]
public sealed class AppearanceMetrics : INotifyPropertyChanged
{
    public static AppearanceMetrics Instance { get; private set; } = new();

    public AppearanceMetrics() => Instance = this;

    public event PropertyChangedEventHandler? PropertyChanged;

    public CornerRadius CardRadius { get; private set; } = new(8);
    public CornerRadius ControlRadius { get; private set; } = new(4);
    public Thickness CardPadding { get; private set; } = new(16);

    public void Set(AppearancePrefs p)
    {
        CardRadius = new CornerRadius(AppearanceMath.CardRadius(p.Corners));
        ControlRadius = new CornerRadius(AppearanceMath.ControlRadius(p.Corners));
        CardPadding = new Thickness(AppearanceMath.CardPadding(p.Density));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardRadius)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ControlRadius)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardPadding)));
    }
}
