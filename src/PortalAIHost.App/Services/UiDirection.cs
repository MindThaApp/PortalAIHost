using Microsoft.UI.Xaml;

namespace PortalAIHost.App.Services;

/// <summary>Applies the UI language's layout direction to window content and dialogs.</summary>
public static class UiDirection
{
    public static FlowDirection Current
    {
        get
        {
            string? language = null;
            try
            {
                var languages = Windows.Globalization.ApplicationLanguages.Languages;
                if (languages.Count > 0) language = languages[0];
            }
            catch
            {
                // Unpackaged or early start: fall back to left-to-right.
            }
            return UiDirectionRule.IsRightToLeft(language) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        }
    }

    public static void Apply(FrameworkElement? element)
    {
        if (element is not null) element.FlowDirection = Current;
    }

    public static void Apply(Window window) => Apply(window.Content as FrameworkElement);
}
