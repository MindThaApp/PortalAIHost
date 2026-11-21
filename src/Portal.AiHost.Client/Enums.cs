namespace Portal.AiHost.Client;

/// <summary>What the client knows about the host and its own access.</summary>
public enum AiHostState
{
    /// <summary>Not checked yet.</summary>
    Unknown,

    /// <summary>The host package is not installed.</summary>
    NotInstalled,

    /// <summary>The host is installed but not running.</summary>
    NotRunning,

    /// <summary>The host is running but this app has no token; call <c>RequestAccessAsync</c>.</summary>
    NeedsAccess,

    /// <summary>The user denied this app.</summary>
    Denied,

    /// <summary>The user removed this app's access; the token was deleted.</summary>
    Revoked,

    /// <summary>The host is reachable and this app is allowed, but no model is installed yet.</summary>
    NoModel,

    /// <summary>Everything is ready.</summary>
    Ready,
}

/// <summary>Request priority (<c>X-Portal-Priority</c>).</summary>
public enum AiPriority
{
    /// <summary>A person is waiting for the answer.</summary>
    Interactive,

    /// <summary>Background work such as bulk summaries.</summary>
    Batch,
}

/// <summary>What to do when the model is not loaded (<c>X-Portal-Load</c>).</summary>
public enum AiLoadBehavior
{
    /// <summary>Wait for the model to load, within the deadline.</summary>
    Wait,

    /// <summary>Fail fast with <c>model_loading</c> while loading starts.</summary>
    Fail,
}

/// <summary>Which model produced a result.</summary>
public enum AiSource
{
    /// <summary>Portal AI Host.</summary>
    Host,

    /// <summary>The caller's fallback model.</summary>
    Fallback,
}
