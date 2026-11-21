using System.Text.Json;
using System.Text.Json.Serialization;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Auth;
using Portal.AiHost.Core.Runtime;
using Portal.AiHost.Core.Scheduling;
using Portal.AiHost.Core.Settings;
using Portal.AiHost.Platform.Windows.Pipes;
using Portal.AiHost.Server;

namespace PortalAIHost.Service;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class ServiceJsonContext : JsonSerializerContext;

public sealed class HostShellOptions
{
    public required HostPaths Paths { get; init; }

    /// <summary>Engines per kind; the composition root passes the real supervisors, tests pass fakes.</summary>
    public required IReadOnlyDictionary<string, IEngineSupervisor> Supervisors { get; init; }

    public required IHostKeyProtector KeyProtector { get; init; }

    public ICallerIdentityResolver? CallerResolver { get; init; }

    public IHardwareProbe? Hardware { get; init; }

    public IModelStore? Store { get; init; }

    public string HostVersion { get; init; } = "1.0.0";

    public TimeSpan QuitGrace { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Overrides the port list (tests).</summary>
    public IReadOnlyList<int>? Ports { get; init; }
}

/// <summary>
/// The running host: runtime, HTTP server, admin pipe and safe-mode marker. <see cref="StartAsync"/> returns null
/// when another host already owns this user's pipe (the caller then sends <c>show</c> and exits).
/// </summary>
public sealed class HostShell : IAsyncDisposable
{
    private readonly HostShellOptions _o;
    private readonly TaskCompletionSource _quit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ControlPipeServer _pipe = null!;
    private ClientRegistry _registry = null!;
    private FairScheduler _scheduler = null!;
    private SettingsStore<HostSettings> _settings = null!;
    private SafeModeMarker _marker = null!;
    private int _quitting;

    private HostShell(HostShellOptions options) => _o = options;

    public AiHostRuntime Runtime { get; private set; } = null!;

    public AiHostServer Server { get; private set; } = null!;

    public SafeModeMarker Marker => _marker;

    public ControlPipeServer Pipe => _pipe;

    public SettingsStore<HostSettings> Settings => _settings;

    public IModelStore? Store => _o.Store;

    /// <summary>The window asked to be shown (pipe <c>show</c>, tray Open, or an approval request).</summary>
    public event EventHandler<string?>? ShowRequested;

    /// <summary>Completes after a clean quit.</summary>
    public Task Completion => _done.Task;

    public static async Task<HostShell?> StartAsync(HostShellOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var shell = new HostShell(options);
        var handler = new ControlHandler(shell);
        var pipe = ControlPipeServer.TryStart(options.Paths.PipeName, handler.HandleAsync);
        if (pipe is null)
        {
            return null;
        }

        shell._pipe = pipe;
        try
        {
            await shell.StartCoreAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return shell;
    }

    private async Task StartCoreAsync(CancellationToken ct)
    {
        var root = _o.Paths.DataRoot;
        _marker = SafeModeMarker.Begin(root);
        _settings = HostSettingsFile.Create(root);
        var settings = _settings.Load();
        _registry = new ClientRegistry(new ClientRegistryOptions { DataDirectory = root }, _o.KeyProtector);
        _scheduler = new FairScheduler();
        Runtime = await AiHostRuntime.StartAsync(new AiHostRuntimeOptions
        {
            Registry = _registry,
            Scheduler = _scheduler,
            Supervisors = _o.Supervisors,
            Hardware = _o.Hardware,
            Planner = new() { MaxMemoryPercent = settings.MaxMemoryPercent, Backend = settings.Backend },
            HostVersion = _o.HostVersion,
            SafeMode = _marker.SafeMode,
        }, ct).ConfigureAwait(false);

        Runtime.EngineStateChanged += (_, change) => _ = PushEngineStateAsync(change);
        _registry.RequestPending += (_, info) => _ = PushApprovalAsync(info);

        await LoadActiveModelsAsync(ct).ConfigureAwait(false);
        Server = await AiHostServer.StartAsync(Runtime, new AiHostServerOptions
        {
            TestMode = _o.Paths.TestMode,
            Ports = _o.Ports,
            DiscoveryPath = _o.Paths.DiscoveryPath,
            CallerResolver = _o.CallerResolver,
        }, ct).ConfigureAwait(false);

        var keepLoaded = settings.Kinds.Where(k => k.Value.KeepLoaded).Select(k => k.Key).ToList();
        _ = Runtime.AutoLoadAsync(keepLoaded, CancellationToken.None);
    }

    public ControlStatus BuildControlStatus() => new()
    {
        Status = Runtime.BuildStatus(null),
        SafeMode = Runtime.SafeMode,
        UncleanExits = _marker.UncleanExits,
        BaseUrl = Server?.BaseUrl,
    };

    public void RequestShow(string? page) => ShowRequested?.Invoke(this, page);

    /// <summary>Starts a clean quit (tray Quit or pipe <c>quit</c>); safe to call more than once.</summary>
    public void RequestQuit() => _ = QuitAsync();

    public async Task QuitAsync()
    {
        if (Interlocked.Exchange(ref _quitting, 1) != 0)
        {
            await _done.Task.ConfigureAwait(false);
            return;
        }

        _quit.TrySetResult();
        try
        {
            await Runtime.StopAsync(_o.QuitGrace).ConfigureAwait(false);
            await Server.StopAsync().ConfigureAwait(false);
            await _pipe.DisposeAsync().ConfigureAwait(false);
            await Runtime.DisposeAsync().ConfigureAwait(false);
            _registry.Dispose();
            _scheduler.Dispose();
            _marker.EndClean();
        }
        finally
        {
            _done.TrySetResult();
        }
    }

    public Task OnResumeAsync() => Runtime.CheckEnginesAfterResumeAsync(CancellationToken.None);

    // ---- active models ------------------------------------------------------------------------

    private string ActiveModelsPath => Path.Combine(_o.Paths.DataRoot, "active-models.json");

    private async Task LoadActiveModelsAsync(CancellationToken ct)
    {
        if (_o.Store is null || !File.Exists(ActiveModelsPath))
        {
            return;
        }

        Dictionary<string, string>? map;
        try
        {
            map = JsonSerializer.Deserialize(File.ReadAllBytes(ActiveModelsPath), ServiceJsonContext.Default.DictionaryStringString);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return;
        }

        foreach (var (kind, id) in map ?? [])
        {
            if (await _o.Store.GetAsync(id, ct).ConfigureAwait(false) is { IsComplete: true } stored && stored.Kind == kind)
            {
                await Runtime.SetActiveAsync(kind, ToActive(stored), ct).ConfigureAwait(false);
            }
        }
    }

    internal async Task ActivateAsync(string modelId, CancellationToken ct)
    {
        var stored = _o.Store is null ? null : await _o.Store.GetAsync(modelId, ct).ConfigureAwait(false);
        if (stored is not { IsComplete: true })
        {
            throw new ControlPipeException(ErrorCodes.NotFound, "That model is not installed.");
        }

        await Runtime.SetActiveAsync(stored.Kind, ToActive(stored), ct).ConfigureAwait(false);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kind in new[] { ModelKinds.Chat, ModelKinds.Embedding })
        {
            if (Runtime.GetActive(kind) is { } m)
            {
                map[kind] = m.ModelId;
            }
        }

        AtomicFile.WriteAllBytes(ActiveModelsPath, JsonSerializer.SerializeToUtf8Bytes(map, ServiceJsonContext.Default.DictionaryStringString));
        await PushStatusAsync().ConfigureAwait(false);
    }

    private static ActiveModel ToActive(StoredModel m) =>
        new(m.Kind, m.Id, m.DisplayName, m.Files.Select(f => f.Path).ToList());

    // ---- pushes -------------------------------------------------------------------------------

    private async Task PushEngineStateAsync(EngineStateChange change)
    {
        try
        {
            await _pipe.BroadcastAsync(ControlTypes.EventEngineState, JsonSerializer.SerializeToElement(new EngineStateEvent
            {
                Kind = change.Kind,
                State = change.State.ToString().ToLowerInvariant(),
                Progress = change.Progress,
            }, AiHostJsonContext.Default.EngineStateEvent)).ConfigureAwait(false);
            await PushStatusAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal Task PushStatusAsync() =>
        _pipe.BroadcastAsync(ControlTypes.EventStatusChanged,
            JsonSerializer.SerializeToElement(BuildControlStatus(), AiHostJsonContext.Default.ControlStatus));

    private async Task PushApprovalAsync(AccessRequestInfo info)
    {
        try
        {
            await _pipe.BroadcastAsync(ControlTypes.EventApprovalPending,
                JsonSerializer.SerializeToElement(ControlHandler.ToPending(info), AiHostJsonContext.Default.PendingApproval))
                .ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        RequestShow("approvals");
    }

    public async ValueTask DisposeAsync() => await QuitAsync().ConfigureAwait(false);
}
