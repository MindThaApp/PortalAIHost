using System.Text.Json;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Platform.Windows.Pipes;
using Windows.ApplicationModel;

namespace PortalAIHost.App.Services;

/// <summary>Opens the current user's control pipe with a short timeout.</summary>
public sealed class PipeControlChannelFactory : IControlChannelFactory
{
    private readonly string? _pipeName;
    private readonly TimeSpan _timeout;

    public PipeControlChannelFactory(string? pipeName = null, TimeSpan? timeout = null)
    {
        _pipeName = pipeName;
        _timeout = timeout ?? TimeSpan.FromMilliseconds(400);
    }

    public async Task<IControlChannel?> TryConnectAsync(CancellationToken ct)
    {
        try
        {
            var client = await ControlPipeClient.ConnectAsync(_pipeName, _timeout, ct).ConfigureAwait(false);
            return new PipeControlChannel(client);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary><see cref="IControlChannel"/> over <see cref="ControlPipeClient"/>.</summary>
public sealed class PipeControlChannel : IControlChannel
{
    private readonly ControlPipeClient _client;

    public PipeControlChannel(ControlPipeClient client)
    {
        _client = client;
        _client.EventReceived += (_, e) => EventReceived?.Invoke(e);
        _client.Disconnected += (_, _) => Disconnected?.Invoke();
    }

    public event Action<ControlEnvelope>? EventReceived;
    public event Action? Disconnected;

    public async Task<ControlEnvelope> ExchangeAsync(string type, JsonElement? payload, CancellationToken ct)
    {
        try
        {
            return await _client.ExchangeAsync(type, payload, ct).ConfigureAwait(false);
        }
        catch (ControlPipeException ex)
        {
            throw new HostRequestException(ex.Code, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            throw new HostUnavailableException();
        }
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}

/// <summary>Start with Windows through the package's StartupTask. Unpackaged runs report "unavailable".</summary>
public sealed class WindowsStartupControl : IStartupControl
{
    /// <summary>TaskId of the windows.startupTask extension in the package manifest.</summary>
    public const string TaskId = "PortalAIHostStartup";

    public async Task<string> GetStateAsync()
    {
        var task = await FindAsync().ConfigureAwait(false);
        return task is null ? StartupStates.Unavailable : Map(task.State);
    }

    public async Task<string> SetEnabledAsync(bool enabled)
    {
        var task = await FindAsync().ConfigureAwait(false);
        if (task is null)
        {
            return StartupStates.Unavailable;
        }

        if (enabled)
        {
            // Windows refuses (and shows nothing) when the user turned it off in Task Manager; the state says so.
            return Map(await task.RequestEnableAsync());
        }

        task.Disable();
        return Map(task.State);
    }

    private static async Task<StartupTask?> FindAsync()
    {
        if (!IsPackaged())
        {
            return null;
        }

        try
        {
            return await StartupTask.GetAsync(TaskId);
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException)
        {
            return null; // The manifest has no startup task with this id.
        }
    }

    private static bool IsPackaged()
    {
        try
        {
            return Package.Current is not null;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string Map(StartupTaskState s) => s switch
    {
        StartupTaskState.Enabled => StartupStates.Enabled,
        StartupTaskState.DisabledByUser => StartupStates.DisabledByUser,
        StartupTaskState.DisabledByPolicy => StartupStates.DisabledByPolicy,
        StartupTaskState.EnabledByPolicy => StartupStates.EnabledByPolicy,
        _ => StartupStates.Disabled,
    };
}
