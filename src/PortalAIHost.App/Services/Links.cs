namespace PortalAIHost.App.Services;

/// <summary>Web addresses and the version shown in the app. Change the privacy address here only.</summary>
public static class Links
{
    public const string PrivacyUrl = "https://mindthaapp.github.io/portal-ai-host/privacy.html";

    /// <summary>Public issue tracker used for feedback and suggestions.</summary>
    public const string FeedbackUrl = "https://github.com/MindThaApp/portal-ai-host-privacy/issues";

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
