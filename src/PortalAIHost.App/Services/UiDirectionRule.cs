namespace PortalAIHost.App.Services;

/// <summary>Decides the layout direction for a UI language tag. Only Arabic is shipped as right-to-left.</summary>
public static class UiDirectionRule
{
    public static bool IsRightToLeft(string? language) =>
        !string.IsNullOrWhiteSpace(language)
        && (language.Equals("ar", StringComparison.OrdinalIgnoreCase)
            || language.StartsWith("ar-", StringComparison.OrdinalIgnoreCase));
}
