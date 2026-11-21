namespace PortalAIHost.App.Services;

/// <summary>Placeholder launcher for the real host.</summary>
public sealed class ProcessHostLauncher : IHostLauncher
{
    // TODO(integration): start the host (the portal-ai-host:start protocol, or the host exe next to this app) and
    // return true once the request was made. Never kill or replace a host that is already running.
    public Task<bool> StartHostAsync(CancellationToken ct = default) => Task.FromResult(false);
}

/// <summary>Launcher used with the fake host: either "starts" the fake or reports that it could not.</summary>
public sealed class FakeHostLauncher : IHostLauncher
{
    private readonly FakeHostControl _host;
    private readonly bool _succeeds;

    public FakeHostLauncher(FakeHostControl host, bool succeeds)
    {
        _host = host;
        _succeeds = succeeds;
    }

    public int Calls { get; private set; }

    public Task<bool> StartHostAsync(CancellationToken ct = default)
    {
        Calls++;
        if (_succeeds) _host.HostStarted();
        return Task.FromResult(_succeeds);
    }
}
