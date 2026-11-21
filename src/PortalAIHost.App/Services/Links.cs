namespace PortalAIHost.App.Services;

/// <summary>Web addresses and the version shown in the app. Change the privacy address here only.</summary>
public static class Links
{
    public const string PrivacyUrl = "https://mindthaapp.github.io/portal-ai-host/privacy.html";

    /// <summary>Feedback and suggestions go to the contact address on the privacy page.</summary>
    public const string FeedbackUrl = "mailto:mindthaapp@gmail.com?subject=Portal%20AI%20Host%20feedback";

    public const string LlamaCppUrl = "https://github.com/ggml-org/llama.cpp";

    /// <summary>The release whose What's new notes ship in this build.</summary>
    public const string WhatsNewVersion = "1.0";

    /// <summary>Version as three numbers, for example 1.0.0.</summary>
    public static string AppVersion
    {
        get
        {
            var v = typeof(Links).Assembly.GetName().Version ?? new Version(1, 0, 0);
            return $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
        }
    }
}
