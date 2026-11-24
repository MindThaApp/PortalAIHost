using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Remote;
using Portal.AiHost.Core.Remote.Client;
using Portal.AiHost.Core.Runtime;
using Portal.AiHost.FakeEngine;

namespace Portal.AiHost.Server.Tests;

/// <summary>A PC that can be switched off: while <see cref="Up"/> is false every connection fails before anything is sent.</summary>
internal sealed class FlakyPc : IRemoteConnector
{
    public volatile bool Up = true;
    public int Opened;
    public int Pings;

    public IRemoteChannel Open(string address, int port, Func<byte[], bool> acceptCertificate) => new Channel(this);

    private sealed class Channel(FlakyPc owner) : IRemoteChannel
    {
        public Task<RemoteReply> PostJsonAsync(string path, string json, CancellationToken ct) => throw new NotSupportedException();

        public Task<RemoteReply> SendAsync(RemoteRequest request, CancellationToken ct)
        {
            if (!owner.Up)
            {
                throw new HttpRequestException(HttpRequestError.ConnectionError, "down");
            }

            Interlocked.Increment(ref owner.Pings);
            return Task.FromResult(new RemoteReply(200, "{}"));
        }

        public Task<RemoteStreamReply> OpenAsync(RemoteRequest request, CancellationToken ct)
        {
            if (!owner.Up)
            {
                throw new HttpRequestException(HttpRequestError.ConnectionError, "down");
            }

            Interlocked.Increment(ref owner.Opened);
            return Task.FromResult(ScriptedRemote.Json(200, """{"id":"from-the-pc"}"""));
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>The relay as a fake: counts opens and can answer "over budget" a set number of times.</summary>
internal sealed class FakeRelayConnector : IRemoteConnector
{
    public int Opened;
    public int Requests;
    public volatile int BusyAnswers;

    public IRemoteChannel Open(string address, int port, Func<byte[], bool> acceptCertificate)
    {
        Interlocked.Increment(ref Opened);
        return new Channel(this);
    }

    private sealed class Channel(FakeRelayConnector owner) : IRemoteChannel
    {
        public Task<RemoteReply> PostJsonAsync(string path, string json, CancellationToken ct) => throw new NotSupportedException();

        public Task<RemoteReply> SendAsync(RemoteRequest request, CancellationToken ct) => Task.FromResult(new RemoteReply(200, "{}"));

        public Task<RemoteStreamReply> OpenAsync(RemoteRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref owner.Requests);
            if (Interlocked.Decrement(ref owner.BusyAnswers) >= 0)
            {
                throw new RelayException(RemoteLinkReasons.RelayBusy, RelayException.BudgetRetryMs, "over budget");
            }

            return Task.FromResult(ScriptedRemote.Json(200, """{"id":"via-relay"}"""));
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>Laptop-model fallback, the one-time prompt, relay mode and the batch relay_busy retry (docs/client-mode-v1.md 1.3, 3.1, 4.6).</summary>
public sealed class FallbackTests : IAsyncLifetime
{
    private const string Chat = """{ "messages": [ { "role": "user", "content": "Hi" } ] }""";
    private const string ChatStream = """{ "messages": [ { "role": "user", "content": "Hi" } ], "stream": true }""";

    private readonly string _stateRoot = Path.Combine(Path.GetTempPath(), "pah-fallback-" + Guid.NewGuid().ToString("N"));
    private readonly FlakyPc _pc = new();
    private readonly FakeRelayConnector _relayPc = new();
    private readonly StepTime _time = new(DateTimeOffset.UtcNow);
    private readonly List<(string? Fallback, string RelayMode, bool Shown)> _saved = [];
    private readonly List<RemoteHostLink> _links = [];
    private TestHost _laptop = null!;
    private RemoteAccess _remote = null!;
    private string _token = null!;
    private ClientModeController? _mode;
    private bool _withRelayAddress;

    public async ValueTask InitializeAsync()
    {
        await Task.CompletedTask;
    }

    private async Task StartAsync(FakeEngineOptions? engine = null, string? fallback = null, string relayMode = RelayModes.Auto,
        bool promptShown = false, bool withRelayAddress = false)
    {
        _withRelayAddress = withRelayAddress;
        _laptop = await TestHost.StartAsync(engine ?? new FakeEngineOptions());
        _token = await _laptop.GetTokenAsync();
        _remote = new RemoteAccess(_laptop.Runtime, new RemoteAccessOptions
        {
            DataDirectory = _laptop.RootPath,
            KeyProtector = new TestKeyProtector(),
            RevokeDevice = id => _laptop.Registry.RevokeDevice(id),
            BindAddress = IPAddress.Loopback,
            AllowSource = _ => true,
            PortOverride = 0,
            HostName = "Laptop",
            Addresses = () => ["192.168.1.30"],
        });
        await _remote.ApplyAsync(new RemoteSettings { Enabled = false });
        var store = new ClientStateStore(_stateRoot, new XorSecretProtector());
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        store.SavePairing(new ClientRemote("Desk PC", "192.168.1.20", 48123, withRelayAddress ? "https://relay.example.invalid" : null,
            RemoteCodec.Encode(SHA256.HashData("pc"u8)), "dev_1", DateTimeOffset.UtcNow), key.ExportPkcs8PrivateKey());
        store.SaveToken("host-token");
        _mode = NewController(fallback, relayMode, promptShown);
    }

    private ClientModeController NewController(string? fallback, string relayMode, bool promptShown) =>
        new(_laptop.Runtime, _remote, new ClientModeOptions
        {
            CreateLink = () =>
            {
                var link = new RemoteHostLink(new ClientStateStore(_stateRoot, new XorSecretProtector()), _pc, _time,
                    new RemoteHostLinkOptions { WatchNetwork = false, Jitter = () => 1.0 },
                    _withRelayAddress ? (_, _) => _relayPc : null);
                _links.Add(link);
                return link;
            },
            PersistMode = _ => { },
            PersistPrefs = (f, r, s) => _saved.Add((f, r, s)),
            InitialFallback = fallback,
            InitialRelayMode = relayMode,
            InitialPromptShown = promptShown,
            Time = _time,
            DrainGrace = TimeSpan.FromSeconds(2),
        });

    public async ValueTask DisposeAsync()
    {
        if (_mode is not null)
        {
            await _mode.DisposeAsync();
        }

        foreach (var link in _links)
        {
            link.Dispose();
        }

        if (_remote is not null)
        {
            await _remote.DisposeAsync();
        }

        if (_laptop is not null)
        {
            await _laptop.DisposeAsync();
        }

        if (Directory.Exists(_stateRoot)) // Test-owned temp folder.
        {
            Directory.Delete(_stateRoot, recursive: true);
        }
    }

    private async Task<(HttpStatusCode Status, string Body)> AppAsync(string json = Chat, string? priority = null)
    {
        using var request = _laptop.Authed(HttpMethod.Post, Routes.ChatCompletions, _token, json);
        if (priority is not null)
        {
            request.Headers.Add(HeaderNames.Priority, priority);
        }

        using var response = await _laptop.Http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private RemoteHostLink Link => _links[^1];

    private async Task GoClientAsync()
    {
        var status = await _mode!.SetModeAsync(HostModes.Client);
        Assert.Null(status.Refused);
    }

    private static string ErrorCode(string body) => JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("code").GetString()!;

    // ---- Fallback ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Default_none_answers_503_remote_unreachable_and_loads_nothing_on_the_laptop()
    {
        await StartAsync();
        await GoClientAsync();
        _pc.Up = false;

        var (status, body) = await AppAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("remote_unreachable", ErrorCode(body));
        Assert.Equal("none", _mode!.Fallback);
        Assert.Equal(0, _laptop.Chat.LoadCount);
        Assert.Equal(EngineState.Stopped, _laptop.Chat.State);
        Assert.False(_mode.Snapshot().UsingLaptopModel);
    }

    [Fact]
    public async Task Local_loads_on_demand_only_when_the_pc_is_unreachable_and_never_before()
    {
        await StartAsync(fallback: ClientFallbackModes.Local);
        await GoClientAsync();

        // The PC is reachable: the request goes there and the laptop engine stays off.
        Assert.Equal(HttpStatusCode.OK, (await AppAsync()).Status);
        Assert.Equal(1, _pc.Opened);
        Assert.Equal(0, _laptop.Chat.LoadCount);

        _pc.Up = false;
        var (status, _) = await AppAsync();

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, _laptop.Chat.LoadCount);
        Assert.Equal(EngineState.Ready, _laptop.Chat.State);
        Assert.True(_mode!.Snapshot().UsingLaptopModel);
        Assert.Equal(1, _pc.Opened);
    }

    [Fact]
    public async Task Once_the_pc_is_known_to_be_away_new_requests_go_straight_to_the_laptop_model()
    {
        await StartAsync(fallback: ClientFallbackModes.Local);
        await GoClientAsync();
        _pc.Up = false;
        Assert.Equal(HttpStatusCode.OK, (await AppAsync()).Status);
        Assert.Equal(RemoteLinkState.Unreachable, Link.Status.State);
        _pc.Opened = 0;

        Assert.Equal(HttpStatusCode.OK, (await AppAsync()).Status);

        Assert.Equal(0, _pc.Opened);
        Assert.Equal(1, _laptop.Chat.LoadCount);
    }

    [Fact]
    public async Task New_requests_return_to_the_pc_when_it_is_reachable_and_the_model_is_left_to_the_idle_rule()
    {
        await StartAsync(fallback: ClientFallbackModes.Local);
        await GoClientAsync();
        _pc.Up = false;
        await AppAsync();
        Assert.True(_mode!.Snapshot().UsingLaptopModel);

        _pc.Up = true;
        Assert.True(await Link.ReconnectAsync());
        var (status, body) = await AppAsync();

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("from-the-pc", body);
        Assert.False(_mode.Snapshot().UsingLaptopModel);
        // Switching back does not unload anything by itself: the engine's normal idle rule does that later.
        Assert.Equal(EngineState.Ready, _laptop.Chat.State);
        Assert.Equal(1, _laptop.Chat.LoadCount);
    }

    [Fact]
    public async Task A_running_local_stream_is_not_moved_when_the_pc_comes_back()
    {
        await StartAsync(new FakeEngineOptions { TokenDelay = TimeSpan.FromMilliseconds(60), Reply = "one two three four five six seven eight" },
            fallback: ClientFallbackModes.Local);
        await GoClientAsync();
        _pc.Up = false;
        using var request = _laptop.Authed(HttpMethod.Post, Routes.ChatCompletions, _token, ChatStream);
        using var response = await _laptop.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(_mode!.Snapshot().UsingLaptopModel);

        _pc.Up = true;
        Assert.True(await Link.ReconnectAsync());
        var text = await response.Content.ReadAsStringAsync(); // The stream ends where it started.

        Assert.Contains("[DONE]", text);
        Assert.DoesNotContain("from-the-pc", text);
        Assert.Equal(0, _pc.Opened);
        var next = await AppAsync();
        Assert.Contains("from-the-pc", next.Body); // The next new request is on the PC.
    }

    [Fact]
    public async Task A_laptop_that_cannot_load_passes_its_own_error_on()
    {
        await StartAsync(new FakeEngineOptions { FailLoadWithCode = "insufficient_memory" }, fallback: ClientFallbackModes.Local);
        await GoClientAsync();
        _pc.Up = false;

        var (status, body) = await AppAsync();

        Assert.NotEqual(HttpStatusCode.OK, status);
        Assert.Equal("insufficient_memory", ErrorCode(body));
    }

    [Fact]
    public async Task A_paused_pc_falls_back_like_an_unreachable_one()
    {
        await StartAsync(fallback: ClientFallbackModes.Local);
        await GoClientAsync();
        Assert.Equal(HttpStatusCode.OK, (await AppAsync()).Status);
        var paused = new ScriptedRemote
        {
            OnOpen = (_, _) => Task.FromResult(ScriptedRemote.Json(503, """{"error":{"code":"device_paused","message":"x","retry_after_ms":4000}}""")),
        };
        using var link = new RemoteHostLink(new ClientStateStore(_stateRoot, new XorSecretProtector()), paused,
            options: new RemoteHostLinkOptions { WatchNetwork = false });
        _laptop.Runtime.SetBackend(new FallbackRouter(link, _laptop.Runtime.LocalInferenceBackend, () => ClientFallbackModes.Local));

        var local = await AppAsync();
        _laptop.Runtime.SetBackend(new FallbackRouter(link, _laptop.Runtime.LocalInferenceBackend, () => ClientFallbackModes.None));
        var none = await AppAsync();

        Assert.Equal(HttpStatusCode.OK, local.Status);
        Assert.Equal(1, _laptop.Chat.LoadCount);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, none.Status);
        Assert.Equal("remote_unreachable", ErrorCode(none.Body));
    }

    [Fact]
    public async Task Changing_the_setting_applies_to_the_next_request_and_is_saved()
    {
        await StartAsync();
        await GoClientAsync();
        _pc.Up = false;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await AppAsync()).Status);

        _mode!.SetFallback(ClientFallbackModes.Local);

        Assert.Equal(HttpStatusCode.OK, (await AppAsync()).Status);
        Assert.Equal((ClientFallbackModes.Local, RelayModes.Auto, true), _saved[^1]);
        Assert.Throws<ArgumentException>(() => _mode.SetFallback("everything"));
    }

    // ---- One-time prompt -----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_prompt_is_raised_once_while_no_fallback_was_chosen_and_stays_until_it_is_shown()
    {
        await StartAsync();
        await GoClientAsync();
        Assert.False(_mode!.FallbackPromptPending);
        Assert.True(_mode.Snapshot().FallbackUnset);

        _pc.Up = false;
        await AppAsync();

        Assert.True(_mode.FallbackPromptPending);
        Assert.True(_mode.Snapshot().FallbackPromptPending);
        _mode.MarkPromptShown();
        Assert.False(_mode.FallbackPromptPending);
        Assert.Equal((null, RelayModes.Auto, true), _saved[^1]);

        _pc.Up = true;
        await Link.ReconnectAsync();
        _pc.Up = false;
        await AppAsync();
        Assert.False(_mode.FallbackPromptPending); // Never repeated.
    }

    [Theory]
    [InlineData(ClientFallbackModes.None)]
    [InlineData(ClientFallbackModes.Local)]
    public async Task Choosing_a_fallback_ends_the_prompt_for_good(string choice)
    {
        await StartAsync();
        await GoClientAsync();
        _mode!.SetFallback(choice);
        _pc.Up = false;

        await AppAsync();

        Assert.False(_mode.FallbackPromptPending);
        Assert.False(_mode.Snapshot().FallbackUnset);
        Assert.True(_saved[^1].Shown);
    }

    [Fact]
    public async Task A_prompt_shown_in_an_earlier_run_is_not_raised_again()
    {
        await StartAsync(promptShown: true);
        await GoClientAsync();
        _pc.Up = false;

        await AppAsync();

        Assert.False(_mode!.FallbackPromptPending);
    }

    // ---- Relay mode ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Auto_uses_the_home_network_first_and_the_relay_when_it_fails()
    {
        await StartAsync(withRelayAddress: true);
        await GoClientAsync();
        Assert.Equal(HttpStatusCode.OK, (await AppAsync()).Status);
        Assert.Equal(1, _pc.Opened);
        Assert.Equal(0, _relayPc.Requests);

        _pc.Up = false;
        var (status, body) = await AppAsync();

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("via-relay", body);
    }

    [Fact]
    public async Task Manual_never_touches_the_relay_until_the_session_switch_is_on_and_it_resets_on_restart()
    {
        await StartAsync(relayMode: RelayModes.Manual, withRelayAddress: true);
        await GoClientAsync();
        _pc.Up = false;

        var off = await AppAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, off.Status);
        Assert.Equal("remote_unreachable", ErrorCode(off.Body));
        Assert.Equal(0, _relayPc.Opened);

        _mode!.SetUseRelay(true);
        Assert.True(_mode.Snapshot().UseRelay);
        var on = await AppAsync();
        Assert.Equal(HttpStatusCode.OK, on.Status);
        Assert.Contains("via-relay", on.Body);
        Assert.DoesNotContain(_saved, s => s.RelayMode != RelayModes.Manual);

        // A new run starts with the switch off (it is never saved).
        await using var restarted = NewController(null, RelayModes.Manual, false);
        Assert.False(restarted.Snapshot().UseRelay);
        Assert.Equal(RelayModes.Manual, restarted.RelayMode);
    }

    [Fact]
    public async Task The_relay_mode_setting_is_validated_saved_and_reported()
    {
        await StartAsync();
        Assert.Equal(RelayModes.Auto, _mode!.Snapshot().RelayMode);

        _mode.SetRelayMode(RelayModes.Manual);

        Assert.Equal(RelayModes.Manual, _mode.Snapshot().RelayMode);
        Assert.Equal((null, RelayModes.Manual, false), _saved[^1]);
        Assert.Throws<ArgumentException>(() => _mode.SetRelayMode("sometimes"));
    }

    [Theory]
    [InlineData(RelayModes.Auto)]
    [InlineData(RelayModes.Manual)]
    public async Task A_pairing_without_a_relay_address_never_uses_the_relay(string relayMode)
    {
        await StartAsync(relayMode: relayMode, withRelayAddress: false);
        await GoClientAsync();
        _mode!.SetUseRelay(true);
        _pc.Up = false;

        var (status, _) = await AppAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal(0, _relayPc.Opened);
    }

    // ---- Batch relay_busy retry ----------------------------------------------------------------------------------

    private async Task<RemoteBackend> BackendOnRelayAsync()
    {
        await StartAsync(withRelayAddress: true);
        _pc.Up = false; // Away from home: only the relay can answer.
        var link = new RemoteHostLink(new ClientStateStore(_stateRoot, new XorSecretProtector()), _pc, _time,
            new RemoteHostLinkOptions { WatchNetwork = false, Jitter = () => 1.0 }, (_, _) => _relayPc);
        _links.Add(link);
        var backend = new RemoteBackend(link, new RemoteBackendOptions(), _time);
        _laptop.Runtime.SetBackend(backend);
        return backend;
    }

    [Fact]
    public async Task A_batch_request_that_gets_relay_busy_waits_a_minute_and_retries_once()
    {
        await BackendOnRelayAsync();
        _relayPc.BusyAnswers = 1;

        var pending = AppAsync(priority: "batch");
        await Wait.UntilAsync(() => _relayPc.Requests >= 1, "the first relay attempt");
        await Wait.UntilAsync(() => _time.Pending().Any(d => d > TimeSpan.FromSeconds(30) && d <= TimeSpan.FromSeconds(60)), "the one-minute wait");
        Assert.False(pending.IsCompleted);
        Assert.Equal(1, _relayPc.Requests);

        _time.Advance(TimeSpan.FromSeconds(61));
        var (status, body) = await pending;

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("via-relay", body);
        Assert.Equal(2, _relayPc.Requests);
    }

    [Fact]
    public async Task A_batch_request_is_retried_only_once()
    {
        await BackendOnRelayAsync();
        _relayPc.BusyAnswers = 5;

        var pending = AppAsync(priority: "batch");
        await Wait.UntilAsync(() => _relayPc.Requests >= 1, "the first relay attempt");
        await Wait.UntilAsync(() => _time.Pending().Any(d => d > TimeSpan.FromSeconds(30) && d <= TimeSpan.FromSeconds(60)), "the one-minute wait");
        _time.Advance(TimeSpan.FromSeconds(61));
        var (status, body) = await pending;

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("remote_unreachable", ErrorCode(body));
        Assert.Equal(2, _relayPc.Requests);
    }

    [Fact]
    public async Task An_interactive_request_that_gets_relay_busy_fails_at_once()
    {
        await BackendOnRelayAsync();
        _relayPc.BusyAnswers = 1;

        var (status, body) = await AppAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("remote_unreachable", ErrorCode(body));
        Assert.Equal("relay_busy", JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("details").GetProperty("reason").GetString());
        Assert.Equal(1, _relayPc.Requests);
    }
}
