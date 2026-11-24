using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Remote;
using Portal.AiHost.Core.Remote.Client;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Runtime;
using Portal.AiHost.FakeEngine;
using PortalAIHost.Service;

namespace Portal.AiHost.Server.Tests;

/// <summary>A body the test feeds by hand: events arrive when the test pushes them, a failure looks like a dropped connection.</summary>
internal sealed class PushStream : Stream
{
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
    private byte[] _current = [];
    private int _offset;

    public volatile bool Disposed;

    public void Push(string text) => _chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(text));

    public void Complete() => _chunks.Writer.TryComplete();

    public void Fail() => _chunks.Writer.TryComplete(new IOException("connection reset"));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_offset >= _current.Length)
        {
            try
            {
                if (!await _chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return 0;
                }
            }
            catch (ChannelClosedException ex)
            {
                throw ex.InnerException ?? ex;
            }

            _chunks.Reader.TryRead(out var next);
            _current = next ?? [];
            _offset = 0;
        }

        var n = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsMemory(_offset, n).CopyTo(buffer);
        _offset += n;
        return n;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>The PC, scripted: records every request that would leave the laptop and answers as the test says.</summary>
internal sealed class ScriptedRemote : IRemoteConnector
{
    public readonly List<RemoteRequest> Requests = [];
    public readonly List<CancellationToken> Tokens = [];
    public Func<RemoteRequest, CancellationToken, Task<RemoteStreamReply>> OnOpen { get; set; } =
        (_, _) => Task.FromResult(Json(200, "{}"));
    public Func<byte[], bool>? Accept;

    public static RemoteStreamReply Json(int status, string body, params (string Name, string Value)[] headers) =>
        Reply(status, "application/json; charset=utf-8", new MemoryStream(Encoding.UTF8.GetBytes(body)), headers);

    public static RemoteStreamReply Reply(int status, string contentType, Stream body, params (string Name, string Value)[] headers) =>
        new(status, [new("Content-Type", contentType), .. headers.Select(h => new KeyValuePair<string, string>(h.Name, h.Value))], body);

    public int Count
    {
        get
        {
            lock (Requests)
            {
                return Requests.Count;
            }
        }
    }

    public IRemoteChannel Open(string address, int port, Func<byte[], bool> acceptCertificate)
    {
        Accept = acceptCertificate;
        return new Channel(this);
    }

    private sealed class Channel(ScriptedRemote owner) : IRemoteChannel
    {
        public Task<RemoteReply> PostJsonAsync(string path, string json, CancellationToken ct) => throw new NotSupportedException();

        public Task<RemoteReply> SendAsync(RemoteRequest request, CancellationToken ct) =>
            Task.FromResult(new RemoteReply(403, "{}")); // The host's own token request (refused: no owner is present).

        public Task<RemoteStreamReply> OpenAsync(RemoteRequest request, CancellationToken ct)
        {
            lock (owner.Requests)
            {
                owner.Requests.Add(request);
                owner.Tokens.Add(ct);
            }

            return owner.OnOpen(request, ct);
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>The laptop host in client mode against a scripted PC (docs/client-mode-v1.md section 4).</summary>
public sealed class ForwardingTests : IAsyncLifetime
{
    private const string HostToken = "host-token-for-the-pc";
    private const string Chat = """{ "messages": [ { "role": "user", "content": "Hi" } ] }""";
    private const string ChatStream = """{ "messages": [ { "role": "user", "content": "Hi" } ], "stream": true }""";

    private readonly string _stateRoot = Path.Combine(Path.GetTempPath(), "pah-forward-" + Guid.NewGuid().ToString("N"));
    private readonly ScriptedRemote _pc = new();
    private TestHost _laptop = null!;
    private RemoteHostLink _link = null!;
    private string _appToken = null!;

    public async ValueTask InitializeAsync()
    {
        _laptop = await TestHost.StartAsync(new FakeEngineOptions());
        _appToken = await _laptop.GetTokenAsync();
        var store = new ClientStateStore(_stateRoot, new XorSecretProtector());
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        store.SavePairing(new ClientRemote("Desk PC", "192.168.1.20", 48123, null,
            RemoteCodec.Encode(SHA256.HashData("pc"u8)), "dev_1", DateTimeOffset.UtcNow), key.ExportPkcs8PrivateKey());
        store.SaveToken(HostToken);
        _link = NewLink();
        _laptop.Runtime.SetBackend(new RemoteBackend(_link, new RemoteBackendOptions
        {
            QueuedCommentInterval = TimeSpan.FromMilliseconds(150),
        }));
    }

    private RemoteHostLink NewLink() => new(new ClientStateStore(_stateRoot, new XorSecretProtector()), _pc,
        options: new RemoteHostLinkOptions { WatchNetwork = false });

    public async ValueTask DisposeAsync()
    {
        _link.Dispose();
        await _laptop.DisposeAsync();
        if (Directory.Exists(_stateRoot)) // Test-owned temp folder.
        {
            Directory.Delete(_stateRoot, recursive: true);
        }
    }

    private HttpRequestMessage App(HttpMethod method, string path, string? json = null, string? token = null) =>
        _laptop.Authed(method, path, token ?? _appToken, json);

    private static JsonElement ErrorOf(string body) => JsonDocument.Parse(body).RootElement.GetProperty("error").Clone();

    [Fact]
    public async Task Allowed_endpoint_is_forwarded_with_the_hosts_own_credentials_only()
    {
        _pc.OnOpen = (_, _) => Task.FromResult(ScriptedRemote.Json(200, """{"id":"chatcmpl-1"}""",
            (HeaderNames.Model, "qwen3"), (HeaderNames.RequestId, "req_remote")));
        using var request = App(HttpMethod.Post, Routes.ChatCompletions, Chat);
        request.Headers.Add(HeaderNames.Priority, "batch");
        request.Headers.Add(HeaderNames.ClientRequestId, "abc-1");
        request.Headers.Add("Cookie", "session=secret");
        request.Headers.Add("X-Custom", "local-only");

        using var response = await _laptop.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"id":"chatcmpl-1"}""", await response.Content.ReadAsStringAsync());
        Assert.Equal("qwen3", response.Headers.GetValues(HeaderNames.Model).Single());
        var sent = Assert.Single(_pc.Requests);
        Assert.Equal("POST", sent.Method);
        Assert.Equal(Routes.ChatCompletions, sent.Target);
        Assert.Equal(Chat, Encoding.UTF8.GetString(sent.Body!));
        string Header(string name) => sent.Headers.Single(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
        Assert.Equal("Bearer " + HostToken, Header("Authorization"));
        Assert.Equal("batch", Header(HeaderNames.Priority));
        Assert.Equal("abc-1", Header(HeaderNames.ClientRequestId));
        Assert.Equal("com.mindthaapp.voiceportal", Header(RemoteBackend.ClientAppHeader));
        Assert.InRange(int.Parse(Header(HeaderNames.DeadlineMs)), 1000, 1_800_000);
        Assert.False(string.IsNullOrEmpty(Header(RemoteHeaders.Signature)));
        Assert.DoesNotContain(sent.Headers, h => h.Key is "Cookie" or "X-Custom");
        // The app's own token appears nowhere in what was sent to the PC.
        Assert.DoesNotContain(sent.Headers, h => h.Value.Contains(_appToken, StringComparison.Ordinal));
        Assert.DoesNotContain(_appToken, Encoding.UTF8.GetString(sent.Body!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Endpoints_outside_the_list_are_refused_locally_and_never_sent()
    {
        foreach (var (method, path) in new[] { ("POST", "/host/models"), ("GET", "/host/placement"), ("POST", "/v1/files"), ("GET", "/admin/anything") })
        {
            using var request = App(new HttpMethod(method), path, method == "POST" ? "{}" : null);
            using var response = await _laptop.Http.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("not_found", ErrorOf(await response.Content.ReadAsStringAsync()).GetProperty("code").GetString());
        }

        Assert.Equal(0, _pc.Count);
        Assert.Equal(
            new[] { Routes.Capabilities, Routes.ChatCompletions, Routes.EaseOff, Routes.EaseOff, Routes.Embeddings, Routes.Health,
                Routes.Models, Routes.Performance, Routes.Tokenize, Routes.Warmup }.Order(),
            RemoteBackend.Allowed.Values.Select(v => v.Target).Order());
    }

    [Fact]
    public async Task Local_endpoints_stay_local()
    {
        using var ping = await _laptop.Http.GetAsync(Routes.Ping);
        Assert.Equal(HttpStatusCode.OK, ping.StatusCode);
        using var status = await _laptop.Http.SendAsync(App(HttpMethod.Get, Routes.Status));
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal(0, _pc.Count);
    }

    [Fact]
    public async Task Stream_chunks_arrive_in_order_before_the_stream_ends()
    {
        var body = new PushStream();
        _pc.OnOpen = (_, _) => Task.FromResult(ScriptedRemote.Reply(200, "text/event-stream", body));
        using var response = await _laptop.Http.SendAsync(App(HttpMethod.Post, Routes.ChatCompletions, ChatStream),
            HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        async Task<string?> Next() => await reader.ReadLineAsync(guard.Token);

        body.Push("data: {\"n\":1}\n\n");
        Assert.Equal("data: {\"n\":1}", await Next());
        Assert.Equal("", await Next());

        // The first event reached the app while the PC's stream is still open.
        Assert.False(body.Disposed);
        body.Push(": portal {\"event\":\"queued\",\"ahead\":1}\n\ndata: {\"n\":2}\n\n");
        Assert.StartsWith(": portal", await Next());
        Assert.Equal("data: {\"n\":2}", await Next());
        Assert.Equal("", await Next());

        body.Push("data: [DONE]\n\n");
        body.Complete();
        Assert.Equal("data: [DONE]", await Next());
        Assert.Equal("", await Next());
        Assert.Null(await Next());
        await Wait.UntilAsync(() => body.Disposed, "the PC connection to close");
    }

    [Fact]
    public async Task Slow_pc_gets_queued_comments_from_the_laptop()
    {
        var gate = new TaskCompletionSource();
        var body = new PushStream();
        _pc.OnOpen = async (_, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            return ScriptedRemote.Reply(200, "text/event-stream", body);
        };
        using var response = await _laptop.Http.SendAsync(App(HttpMethod.Post, Routes.ChatCompletions, ChatStream),
            HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        Assert.Equal(": portal {\"event\":\"queued\"}", await reader.ReadLineAsync(guard.Token));

        gate.SetResult();
        body.Push("data: [DONE]\n\n");
        body.Complete();
        var rest = new List<string?>();
        while (await reader.ReadLineAsync(guard.Token) is { } line)
        {
            rest.Add(line);
        }

        Assert.Contains("data: [DONE]", rest);
    }

    [Fact]
    public async Task Closing_the_app_connection_cancels_the_request_to_the_pc()
    {
        var body = new PushStream();
        _pc.OnOpen = (_, _) => Task.FromResult(ScriptedRemote.Reply(200, "text/event-stream", body));
        var response = await _laptop.Http.SendAsync(App(HttpMethod.Post, Routes.ChatCompletions, ChatStream),
            HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        body.Push("data: {\"n\":1}\n\n");
        Assert.Equal("data: {\"n\":1}", await reader.ReadLineAsync());

        response.Dispose(); // The app goes away.

        await Wait.UntilAsync(() => body.Disposed, "the PC connection to close");
        Assert.True(_pc.Tokens.Single().IsCancellationRequested || body.Disposed);
    }

    [Fact]
    public async Task Stream_cut_after_the_headers_ends_with_remote_unreachable_and_no_done()
    {
        var body = new PushStream();
        _pc.OnOpen = (_, _) => Task.FromResult(ScriptedRemote.Reply(200, "text/event-stream", body));
        using var response = await _laptop.Http.SendAsync(App(HttpMethod.Post, Routes.ChatCompletions, ChatStream),
            HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        body.Push("data: {\"n\":1}\n\n");
        body.Fail();

        var lines = new List<string>();
        while (await reader.ReadLineAsync(guard.Token) is { } line)
        {
            lines.Add(line);
        }

        Assert.Contains("data: {\"n\":1}", lines);
        var error = Assert.Single(lines, l => l.StartsWith("data: {\"error\"", StringComparison.Ordinal));
        Assert.Equal("remote_unreachable", ErrorOf(error[6..]).GetProperty("code").GetString());
        Assert.DoesNotContain("data: [DONE]", lines);
        Assert.Equal(1, _pc.Count); // No automatic retry.
        Assert.Equal(RemoteLinkState.Unreachable, _link.Status.State);
    }

    public static TheoryData<string> Reasons() => ["offline", "refused", "clock", "cert_changed", "token", "paused", "quiet_hours", "gaming_busy", "limit_reached"];

    [Theory]
    [MemberData(nameof(Reasons))]
    public async Task Every_unreachable_reason_becomes_503_remote_unreachable(string scenario)
    {
        _pc.OnOpen = (_, _) => scenario switch
        {
            "offline" => throw new HttpRequestException("down"),
            "cert_changed" => CertChanged(),
            "refused" => Task.FromResult(ScriptedRemote.Json(401, DeviceError(DeviceAuthReasons.UnknownDevice))),
            "clock" => Task.FromResult(ScriptedRemote.Json(401, DeviceError(DeviceAuthReasons.StaleTimestamp))),
            "token" => Task.FromResult(ScriptedRemote.Json(401, """{"error":{"code":"token_invalid","message":"x"}}""")),
            _ => Task.FromResult(ScriptedRemote.Json(503, $$$"""{"error":{"code":"{{{PausedCode(scenario)}}}","message":"x","retry_after_ms":4000}}""")),
        };
        var expected = scenario == "token" ? "refused" : scenario;

        using var response = await _laptop.Http.SendAsync(App(HttpMethod.Post, Routes.ChatCompletions, Chat));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var error = ErrorOf(await response.Content.ReadAsStringAsync());
        Assert.Equal("remote_unreachable", error.GetProperty("code").GetString());
        Assert.Equal(expected, error.GetProperty("details").GetProperty("reason").GetString());
    }

    private Task<RemoteStreamReply> CertChanged()
    {
        _pc.Accept!([1, 2, 3]); // A certificate other than the pinned one: the handshake is refused.
        throw new HttpRequestException("pin");
    }

    private static string DeviceError(string reason) =>
        $$$$"""{"error":{"code":"device_unauthorized","message":"x","details":{"reason":"{{{{reason}}}}"}}}""";

    private static string PausedCode(string reason) => reason switch
    {
        "paused" => "device_paused",
        "quiet_hours" => "device_quiet_hours",
        "gaming_busy" => "host_busy_gaming",
        _ => "device_limit_reached",
    };

    [Fact]
    public async Task Other_answers_from_the_pc_pass_through_unchanged()
    {
        const string body = """{"error":{"message":"No model","type":"unavailable","code":"no_model"}}""";
        _pc.OnOpen = (_, _) => Task.FromResult(ScriptedRemote.Json(503, body, (HeaderNames.RetryAfter, "3")));

        using var response = await _laptop.Http.SendAsync(App(HttpMethod.Post, Routes.Embeddings, """{"input":"a"}"""));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Deadline_passes_on_the_laptop_when_the_pc_does_not_answer()
    {
        _pc.OnOpen = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null!;
        };
        using var request = App(HttpMethod.Post, Routes.ChatCompletions, Chat);
        request.Headers.Add(HeaderNames.DeadlineMs, "1000");

        using var response = await _laptop.Http.SendAsync(request);

        Assert.Equal("deadline_exceeded", ErrorOf(await response.Content.ReadAsStringAsync()).GetProperty("code").GetString());
        var sent = Assert.Single(_pc.Requests);
        Assert.Equal("1000", sent.Headers.Single(h => h.Key == HeaderNames.DeadlineMs).Value); // Floor of the remote budget.
    }

    [Fact]
    public async Task Capabilities_come_from_the_pc_are_cached_and_carry_a_hash_revision()
    {
        _pc.OnOpen = (_, _) => Task.FromResult(ScriptedRemote.Json(200, """{"api_version":"1","capabilities_revision":7,"chat":{"available":true}}"""));

        using var first = await _laptop.Http.SendAsync(App(HttpMethod.Get, Routes.Capabilities));
        using var second = await _laptop.Http.SendAsync(App(HttpMethod.Get, Routes.Capabilities));

        Assert.Equal(1, _pc.Count);
        var doc = JsonDocument.Parse(await first.Content.ReadAsStringAsync()).RootElement;
        Assert.True(doc.GetProperty("chat").GetProperty("available").GetBoolean());
        var revision = doc.GetProperty("capabilities_revision").GetInt64();
        Assert.NotEqual(7, revision);
        Assert.Equal(revision.ToString(), first.Headers.GetValues(HeaderNames.CapabilitiesRevision).Single());
        Assert.Equal(revision.ToString(), second.Headers.GetValues(HeaderNames.CapabilitiesRevision).Single());
    }

    [Fact]
    public void App_name_label_is_sanitised()
    {
        Assert.Equal("Unknown app", RemoteBackend.SanitizeAppName(""));
        Assert.Equal("Unknown app", RemoteBackend.SanitizeAppName("\u0001\u0002"));
        Assert.Equal("AB", RemoteBackend.SanitizeAppName("A\r\nB"));
        Assert.Equal(64, RemoteBackend.SanitizeAppName(new string('x', 200)).Length);
    }
}

/// <summary>Mode switch: engine unloaded, listener and bridge off, revision bumped, and back.</summary>
public sealed class ClientModeSwitchTests : IAsyncLifetime
{
    private readonly string _stateRoot = Path.Combine(Path.GetTempPath(), "pah-mode-" + Guid.NewGuid().ToString("N"));
    private readonly ScriptedRemote _pc = new();
    private readonly List<string> _saved = [];
    private TestHost _laptop = null!;
    private RemoteAccess _remote = null!;
    private ClientModeController _mode = null!;
    private string _token = null!;
    private bool _paired = true;

    public async ValueTask InitializeAsync()
    {
        _laptop = await TestHost.StartAsync(new FakeEngineOptions());
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
        await _remote.ApplyAsync(new RemoteSettings { Enabled = true });
        var store = new ClientStateStore(_stateRoot, new XorSecretProtector());
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        store.SavePairing(new ClientRemote("Desk PC", "192.168.1.20", 48123, null,
            RemoteCodec.Encode(SHA256.HashData("pc"u8)), "dev_1", DateTimeOffset.UtcNow), key.ExportPkcs8PrivateKey());
        store.SaveToken("host-token");
        _mode = new ClientModeController(_laptop.Runtime, _remote, new ClientModeOptions
        {
            CreateLink = () => _paired
                ? new RemoteHostLink(new ClientStateStore(_stateRoot, new XorSecretProtector()), _pc,
                    options: new RemoteHostLinkOptions { WatchNetwork = false })
                : null,
            PersistMode = _saved.Add,
            DrainGrace = TimeSpan.FromSeconds(2),
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _mode.DisposeAsync();
        await _remote.DisposeAsync();
        await _laptop.DisposeAsync();
        if (Directory.Exists(_stateRoot)) // Test-owned temp folder.
        {
            Directory.Delete(_stateRoot, recursive: true);
        }
    }

    private async Task<HttpStatusCode> ChatAsync()
    {
        using var request = _laptop.Authed(HttpMethod.Post, Routes.ChatCompletions, _token, """{ "messages": [ { "role": "user", "content": "Hi" } ] }""");
        using var response = await _laptop.Http.SendAsync(request);
        return response.StatusCode;
    }

    [Fact]
    public async Task Entering_client_mode_unloads_the_engine_stops_the_listener_and_bumps_the_revision()
    {
        Assert.Equal(HttpStatusCode.OK, await ChatAsync()); // Loads the local engine.
        Assert.Equal(EngineState.Ready, _laptop.Chat.State);
        Assert.True(_remote.Listening);
        var before = _laptop.Runtime.CapabilitiesRevision;

        var status = await _mode.SetModeAsync(HostModes.Client);

        Assert.Equal(HostModes.Client, status.Mode);
        Assert.Null(status.Refused);
        Assert.Equal("Desk PC", status.PcName);
        Assert.Equal(EngineState.Stopped, _laptop.Chat.State);
        Assert.False(_remote.Listening);
        Assert.True(_remote.Suspended);
        Assert.True(_laptop.Runtime.UsesRemoteBackend);
        Assert.True(_laptop.Runtime.CapabilitiesRevision > before);
        Assert.Equal([HostModes.Client], _saved);

        // Requests now go to the PC, not the engine.
        Assert.Equal(HttpStatusCode.OK, await ChatAsync());
        Assert.Equal(1, _pc.Count);
        Assert.Equal(EngineState.Stopped, _laptop.Chat.State);
    }

    [Fact]
    public async Task Switching_back_restores_the_listener_and_the_local_engine()
    {
        await _mode.SetModeAsync(HostModes.Client);
        var during = _laptop.Runtime.CapabilitiesRevision;

        var status = await _mode.SetModeAsync(HostModes.Server);

        Assert.Equal(HostModes.Server, status.Mode);
        Assert.True(_remote.Listening);
        Assert.False(_remote.Suspended);
        Assert.False(_laptop.Runtime.UsesRemoteBackend);
        Assert.True(_laptop.Runtime.CapabilitiesRevision > during);
        Assert.Equal([HostModes.Client, HostModes.Server], _saved);
        var requestsToPc = _pc.Count;
        Assert.Equal(HttpStatusCode.OK, await ChatAsync()); // The local engine answers again.
        Assert.Equal(requestsToPc, _pc.Count);
        Assert.Equal(EngineState.Ready, _laptop.Chat.State);
    }

    [Fact]
    public async Task Without_a_pairing_the_switch_is_refused_and_nothing_changes()
    {
        _paired = false;

        var status = await _mode.SetModeAsync(HostModes.Client);

        Assert.Equal("not_paired", status.Refused);
        Assert.Equal(HostModes.Server, status.Mode);
        Assert.True(_remote.Listening);
        Assert.False(_laptop.Runtime.UsesRemoteBackend);
        Assert.Empty(_saved);
        Assert.Equal(HttpStatusCode.OK, await ChatAsync());
    }

    [Fact]
    public async Task Settings_applied_while_in_client_mode_cannot_reopen_the_listener()
    {
        await _mode.SetModeAsync(HostModes.Client);

        await _remote.ApplyAsync(new RemoteSettings { Enabled = true });

        Assert.False(_remote.Listening);
    }

    [Fact]
    public async Task Requests_the_local_engine_is_answering_finish_before_it_is_stopped()
    {
        _laptop.Chat.Options.TokenDelay = TimeSpan.FromMilliseconds(150);
        var running = ChatAsync();
        await Wait.UntilAsync(() => _laptop.Runtime.LocalInFlightRequests > 0, "the local request to start");

        await _mode.SetModeAsync(HostModes.Client);

        Assert.Equal(HttpStatusCode.OK, await running);
    }
}

/// <summary>The mode survives a restart, and the host starts without a listener when it was a client.</summary>
public sealed class ClientModeShellTests
{
    private static void Pair(string dataRoot)
    {
        var store = new ClientStateStore(dataRoot, new XorSecretProtector());
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        store.SavePairing(new ClientRemote("Desk PC", "192.168.1.20", 48123, null,
            RemoteCodec.Encode(SHA256.HashData("pc"u8)), "dev_1", DateTimeOffset.UtcNow), key.ExportPkcs8PrivateKey());
        store.SaveToken("host-token");
    }

    [Fact]
    public async Task Mode_is_saved_and_restored_at_the_next_start_without_opening_a_listener()
    {
        using var root = new TestRoot();
        var pc = new ScriptedRemote();
        var remoteOptions = new RemoteAccessTestOptions { BindAddress = IPAddress.Loopback, AllowSource = _ => true, Port = 0 };
        await using (var first = await ShellHarness.StartAsync(root, remoteOptions, new XorSecretProtector(), pc))
        {
            Pair(first.Paths.DataRoot);
            await first.Shell.Remote.ApplyAsync(new RemoteSettings { Enabled = true });
            Assert.True(first.Shell.Remote.Listening);

            var status = await first.Shell.ClientMode.SetModeAsync(HostModes.Client);

            Assert.True(status.Mode == HostModes.Client, "refused: " + status.Refused);
            Assert.Equal(HostModes.Client, first.Shell.Settings.Current.Mode);
            Assert.False(first.Shell.Remote.Listening);
        }

        await using var second = await ShellHarness.StartAsync(root, remoteOptions, new XorSecretProtector(), pc);
        Assert.Equal(HostModes.Client, second.Shell.ClientMode.Mode);
        Assert.False(second.Shell.Remote.Listening);
        Assert.True(second.Shell.Runtime.UsesRemoteBackend);

        await second.Shell.ClientMode.SetModeAsync(HostModes.Server);
        Assert.Equal(HostModes.Server, second.Shell.Settings.Current.Mode);
        Assert.False(second.Shell.Runtime.UsesRemoteBackend);
    }

    [Fact]
    public async Task A_saved_client_mode_without_a_pairing_starts_in_server_mode()
    {
        using var root = new TestRoot();
        await using (var first = await ShellHarness.StartAsync(root))
        {
            var saved = first.Shell.Settings.Current;
            saved.Mode = HostModes.Client;
            first.Shell.Settings.Save(saved);
        }

        await using var second = await ShellHarness.StartAsync(root, secret: new XorSecretProtector(), connector: new ScriptedRemote());

        Assert.Equal(HostModes.Server, second.Shell.ClientMode.Mode);
        Assert.Equal(HostModes.Server, second.Shell.Settings.Current.Mode);
    }
}
