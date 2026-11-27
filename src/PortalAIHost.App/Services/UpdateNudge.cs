namespace PortalAIHost.App.Services;

/// <summary>What the Store said: it could not be asked, there is no update, or an update with its version.</summary>
public readonly record struct UpdateQueryResult(bool Succeeded, string? Version)
{
    public static UpdateQueryResult Failed => new(false, null);
    public static UpdateQueryResult NoUpdate => new(true, null);
    public static UpdateQueryResult Found(string version) => new(true, version);
}

/// <summary>Where updates come from. The real one asks the Microsoft Store; tests use a fake.</summary>
public interface IUpdateSource
{
    Task<UpdateQueryResult> QueryAsync(CancellationToken ct);
}

/// <summary>The version of the update the user dismissed, kept per user.</summary>
public interface IUpdateNudgeState
{
    string? DismissedVersion { get; }
    void SaveDismissed(string version);
}

/// <summary>Where the update check is allowed to run at all.</summary>
public static class UpdateNudgeGate
{
    /// <summary>Only a packaged install makes a Store call. Dev, fake, check and screenshot runs never do.</summary>
    public static bool IsEnabled(bool packaged, bool fakeRun, bool skipFirstRun, bool customStateFolder, bool screenshotRun) =>
        packaged && !fakeRun && !skipFirstRun && !customStateFolder && !screenshotRun;
}

/// <summary>
/// Decides whether the "update available" bar is shown. The Store is asked at most once per session, and a version the user
/// dismissed is not shown again; a newer version is. Errors are swallowed: a failed check is simply no bar.
/// </summary>
public sealed class UpdateNudgeService
{
    private readonly IUpdateSource _source;
    private readonly IUpdateNudgeState _state;
    private readonly bool _enabled;
    private int _asked;

    public UpdateNudgeService(IUpdateSource source, IUpdateNudgeState state, bool enabled)
    {
        _source = source;
        _state = state;
        _enabled = enabled;
    }

    /// <summary>Runs the check (call it off the UI thread). Returns the version to announce, or null for no bar.</summary>
    public async Task<string?> CheckAsync(CancellationToken ct)
    {
        if (!_enabled) return null;
        if (Interlocked.Exchange(ref _asked, 1) == 1) return null;
        UpdateQueryResult result;
        try
        {
            result = await _source.QueryAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            return null;
        }

        if (!result.Succeeded || string.IsNullOrEmpty(result.Version)) return null;
        return string.Equals(_state.DismissedVersion, result.Version, StringComparison.Ordinal) ? null : result.Version;
    }

    /// <summary>The user closed the bar or pressed Update: this version stays quiet from now on.</summary>
    public void Dismiss(string version)
    {
        try
        {
            _state.SaveDismissed(version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not remembering only means the bar can show once more next session.
        }
    }
}
