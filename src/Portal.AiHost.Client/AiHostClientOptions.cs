namespace Portal.AiHost.Client;

/// <summary>Settings for <see cref="AiHostClient.Create"/>.</summary>
public sealed class AiHostClientOptions
{
    /// <summary>Stable reverse-DNS app id, for example <c>com.mindthaapp.voiceportal</c>.</summary>
    public required string AppId { get; init; }

    /// <summary>The app name shown to the user in the host's approval prompt.</summary>
    public required string AppDisplayName { get; init; }

    /// <summary>Optional app version, shown in the approval prompt.</summary>
    public string? AppVersion { get; init; }

    /// <summary>Optional one-line reason shown in the approval prompt.</summary>
    public string? AccessReason { get; init; }

    /// <summary>Where the token is kept. Default: DPAPI (current user) in the app's own data folder (Windows only).</summary>
    public ITokenStore? TokenStore { get; init; }

    /// <summary>The app's built-in model for when the host cannot answer. Null means no fallback.</summary>
    public IAiFallback? Fallback { get; init; }

    /// <summary>
    /// Vectors from different models do not mix, so embeddings never fall back unless this is true and the
    /// fallback supports them. Default false.
    /// </summary>
    public bool AllowEmbeddingFallback { get; init; }

    /// <summary>Retry settings.</summary>
    public RetryOptions Retry { get; init; } = new();

    /// <summary>Priority used when a call does not set one. Default interactive.</summary>
    public AiPriority DefaultPriority { get; init; } = AiPriority.Interactive;

    /// <summary>Host address to try first (development only). The <c>PORTAL_AI_HOST_URL</c> variable also works.</summary>
    public Uri? DiscoveryOverride { get; init; }

    /// <summary>Discovery details. The defaults are right for production.</summary>
    public HostDiscoveryOptions Discovery { get; init; } = new();

    /// <summary>Receives diagnostic lines. Never receives tokens or prompt or reply text.</summary>
    public Action<string>? Logger { get; init; }

    /// <summary>
    /// Starts the host. Default opens the <c>portal-ai-host:start</c> URI. Return true if a start was requested.
    /// </summary>
    public Func<CancellationToken, Task<bool>>? HostLauncher { get; init; }

    /// <summary>How long <c>TryStartHostAsync</c> waits for the host to appear. Default 10 s.</summary>
    public TimeSpan HostStartWait { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>Retry behaviour (api-v1 section 8).</summary>
public sealed class RetryOptions
{
    /// <summary>Most tries per call, including the first. Default 3.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>First backoff step. Default 250 ms.</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Largest backoff step. Default 4 s.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(4);
}

/// <summary>Where and how the host is looked for.</summary>
public sealed class HostDiscoveryOptions
{
    /// <summary>Placeholder for the Store build's package family name; replace once the identity is reserved.</summary>
    public const string StorePackageFamilyName = "MindThaApp.PortalAIHost_PLACEHOLDER";

    /// <summary>Placeholder for the developer build's package family name.</summary>
    public const string DeveloperPackageFamilyName = "PortalAIHost.Dev_PLACEHOLDER";

    /// <summary>Host package family names to look under, in order. Default: Store build, then developer build.</summary>
    public IReadOnlyList<string> PackageFamilyNames { get; init; } =
        [StorePackageFamilyName, DeveloperPackageFamilyName];

    /// <summary>The folder holding packages' data. Default <c>%LOCALAPPDATA%\Packages</c>.</summary>
    public string? PackagesRoot { get; init; }

    /// <summary>The port tried last. Default 41870.</summary>
    public int DefaultPort { get; init; } = Contracts.ApiInfo.DefaultPort;

    /// <summary>Time allowed for one <c>/host/ping</c>. Default 2 s.</summary>
    public TimeSpan PingTimeout { get; init; } = TimeSpan.FromSeconds(2);
}
