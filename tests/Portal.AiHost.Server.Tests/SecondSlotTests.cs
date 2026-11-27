using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Api;
using Portal.AiHost.Core.Catalogue;
using Portal.AiHost.Core.Remote;
using Portal.AiHost.Core.Runtime;
using Portal.AiHost.Core.Scheduling;
using Portal.AiHost.Core.Tests.Remote;
using Portal.AiHost.FakeEngine;
using Portal.AiHost.Server;

namespace Portal.AiHost.Server.Tests;

/// <summary>
/// The second concurrent model slot (docs/client-mode-v1.md 4.7): two engines at once, routing by model name, per-slot
/// "loaded by", take back, owner action wins, and auto-load of a named model. Real TLS on loopback with signed requests.
/// </summary>
public sealed class SecondSlotTests : IAsyncLifetime
{
    private static readonly string Second = ModelSlots.Second(ModelKinds.Chat);

    private TestHost _host = null!;
    private RemoteAccess _remote = null!;
    private HttpClient _http = null!;
    private FakeModelHost _models = null!;

    public async ValueTask InitializeAsync() => await StartAsync(allowReplace: false);

    private async Task StartAsync(bool allowReplace, FakeEngineOptions? chat2 = null)
    {
        _host = await TestHost.StartAsync(new FakeEngineOptions { }, withModels: false, secondSlot: true, chat2: chat2);
        _models = new FakeModelHost(Path.Combine(_host.RootPath, "models"));
        await _host.Runtime.SetActiveAsync(ModelKinds.Chat,
            new ActiveModel(ModelKinds.Chat, "pc-model", "pc-model", [Path.Combine(_host.RootPath, "models", "pc-model.gguf")]), default);
        _remote = new RemoteAccess(_host.Runtime, new RemoteAccessOptions
        {
            DataDirectory = _host.RootPath,
            KeyProtector = new TestKeyProtector(),
            RevokeDevice = id => _host.Registry.RevokeDevice(id),
            BindAddress = IPAddress.Loopback,
            AllowSource = _ => true,
            PortOverride = 0,
            HostName = "Desk PC",
            Addresses = () => ["192.168.1.20"],
            ModelHost = _models,
        });
        await _remote.ApplyAsync(new RemoteSettings { Enabled = true, Relay = false, AllowReplaceModels = allowReplace });
        var pinned = _remote.Snapshot().Fingerprint;
        _http = new HttpClient(new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    cert is not null && RemoteCodec.Encode(SHA256.HashData(cert.GetRawCertData())) == pinned,
            },
        })
        {
            BaseAddress = new Uri($"https://127.0.0.1:{_remote.Port}"),
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _remote.DisposeAsync();
        await _host.DisposeAsync();
    }

    private static string Pad(string b64Url)
    {
        var s = b64Url.Replace('-', '+').Replace('_', '/');
        return s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private async Task<PairingClient> PairedAsync(string name, bool grant)
    {
        var offer = _remote.StartPairing();
        var client = new PairingClient(name);
        using var started = await _http.PostAsync(RemoteApi.PairStartPath,
            Json(JsonSerializer.Serialize(client.Start(offer.Code), AiHostJsonContext.Default.PairStartRequest)));
        var start = JsonSerializer.Deserialize(await started.Content.ReadAsStringAsync(), AiHostJsonContext.Default.PairStartResponse)!;
        Assert.True(client.VerifyHost(start, offer.Code, Convert.FromBase64String(Pad(offer.Fingerprint))));
        using var finished = await _http.PostAsync(RemoteApi.PairFinishPath,
            Json(JsonSerializer.Serialize(client.Finish(offer.Code, start), AiHostJsonContext.Default.PairFinishRequest)));
        var done = JsonSerializer.Deserialize(await finished.Content.ReadAsStringAsync(), AiHostJsonContext.Default.PairFinishResponse)!;
        Assert.True(client.VerifyDone(done, offer.Code, start));
        if (grant)
        {
            Assert.True(_remote.SetModelControl(client.DeviceId!, true));
        }

        return client;
    }

    private readonly Dictionary<PairingClient, string> _tokens = [];

    /// <summary>The access flow from the device: request, the user approves on this PC, collect the token.</summary>
    private async Task<string> TokenAsync(PairingClient client)
    {
        if (_tokens.TryGetValue(client, out var known))
        {
            return known;
        }

        var (status, created) = await CallAsync(client, HttpMethod.Post, Routes.AuthRequests,
            """{ "app_id": "com.mindthaapp.aiportal", "app_name": "AI Portal" }""");
        Assert.Equal(HttpStatusCode.Accepted, status);
        var id = created.GetProperty("request_id").GetString()!;
        await _host.Registry.ApproveAsync(id, default);
        var (_, polled) = await CallAsync(client, HttpMethod.Get, $"{Routes.AuthRequests}/{id}?wait_ms=1000");
        return _tokens[client] = polled.GetProperty("token").GetString()!;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> ChatAsync(PairingClient client, string json) =>
        await CallAsync(client, HttpMethod.Post, Routes.ChatCompletions, json, await TokenAsync(client));

    private async Task<(HttpStatusCode Status, JsonElement Body)> CallAsync(PairingClient client, HttpMethod method, string target,
        string? json = null, string? token = null)
    {
        var body = json is null ? [] : Encoding.UTF8.GetBytes(json);
        using var message = new HttpRequestMessage(method, target);
        foreach (var (name, value) in client.SignedHeaders(method.Method, target, DateTimeOffset.UtcNow, body))
        {
            message.Headers.TryAddWithoutValidation(name, value);
        }

        if (json is not null)
        {
            message.Content = Json(json);
        }

        if (token is not null)
        {
            message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await _http.SendAsync(message);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static string Load(string id, string? placement = null) =>
        placement is null ? $$"""{ "model_id": "{{id}}" }""" : $$"""{ "model_id": "{{id}}", "placement": "{{placement}}" }""";

    private static string ChatBody(string? model) =>
        model is null
            ? """{ "messages": [ { "role": "user", "content": "hi" } ] }"""
            : $$"""{ "model": "{{model}}", "messages": [ { "role": "user", "content": "hi" } ] }""";

    private static void AssertError(HttpStatusCode status, JsonElement body, HttpStatusCode expected, string code, string? reason = null)
    {
        Assert.Equal(expected, status);
        var error = body.GetProperty("error");
        Assert.Equal(code, error.GetProperty("code").GetString());
        if (reason is not null)
        {
            Assert.Equal(reason, error.GetProperty("details").GetProperty("reason").GetString());
        }
    }

    private async Task LoadPcModelAsync()
    {
        await _host.Runtime.EnsureLoadedAsync(ModelKinds.Chat, default);
        Assert.Equal(EngineState.Ready, _host.Chat.State);
    }

    [Fact]
    public async Task TwoModelsRunAtOnceAndThePcsOwnModelStaysInTheFirstSlot()
    {
        await LoadPcModelAsync();
        var client = await PairedAsync("Laptop", grant: true);

        // allow_replace is off: before the second slot this was in_use_by_host. Now the device's model takes the second engine.
        var (status, answer) = await CallAsync(client, HttpMethod.Post, Routes.DeviceModelLoad, Load("alpha", "cpu"));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("loaded", answer.GetProperty("state").GetString());
        Assert.Equal(EngineState.Ready, _host.Chat.State);
        Assert.Equal(EngineState.Ready, _host.Chat2!.State);
        Assert.Equal("pc-model", _host.Runtime.GetActive(ModelKinds.Chat)!.ModelId);
        Assert.Equal("alpha", _host.Runtime.GetActive(Second)!.ModelId);
        Assert.Equal(Second, _host.Runtime.GetActive(Second)!.Kind);
        Assert.Equal("pc-model", _host.Chat.Current!.ModelId);
        Assert.Equal("alpha", _host.Chat2.Current!.ModelId);

        // "Loaded by" is kept per slot.
        var loaded = Assert.Single(_remote.Snapshot().LoadedModels);
        Assert.Equal("alpha", loaded.ModelId);
        Assert.Equal("cpu", loaded.Placement);
        var (_, list) = await CallAsync(client, HttpMethod.Get, Routes.DeviceModels);
        var byId = list.GetProperty("models").EnumerateArray().ToDictionary(m => m.GetProperty("id").GetString()!);
        Assert.Equal("pc", byId["pc-model"].GetProperty("loaded_by").GetString());
        Assert.Equal(client.DeviceId, byId["alpha"].GetProperty("loaded_by").GetString());
        Assert.Equal("loaded", byId["alpha"].GetProperty("state").GetString());
        Assert.Equal("unloaded", byId["beta"].GetProperty("state").GetString());

        // A third model replaces the device's own second-slot model, never the PC's.
        var (status2, _) = await CallAsync(client, HttpMethod.Post, Routes.DeviceModelLoad, Load("beta"));
        Assert.Equal(HttpStatusCode.OK, status2);
        Assert.Equal("pc-model", _host.Chat.Current!.ModelId);
        Assert.Equal(EngineState.Ready, _host.Chat.State);
        Assert.Equal("beta", _host.Runtime.GetActive(Second)!.ModelId);
        Assert.Equal("beta", _host.Chat2.Current!.ModelId);
        Assert.Equal("beta", Assert.Single(_remote.Snapshot().LoadedModels).ModelId);
    }

    [Fact]
    public async Task ChatRequestsGoToTheSlotThatHoldsTheNamedModel()
    {
        await LoadPcModelAsync();
        var client = await PairedAsync("Laptop", grant: true);
        await CallAsync(client, HttpMethod.Post, Routes.DeviceModelLoad, Load("alpha"));

        var (s1, b1) = await ChatAsync(client, ChatBody("alpha"));
        Assert.Equal(HttpStatusCode.OK, s1);
        Assert.NotNull(_host.Chat2!.LastChatRequest);
        Assert.Null(_host.Chat.LastChatRequest);
        Assert.NotEqual(JsonValueKind.Undefined, b1.ValueKind);

        var (s2, _) = await ChatAsync(client, ChatBody("pc-model"));
        Assert.Equal(HttpStatusCode.OK, s2);
        Assert.NotNull(_host.Chat.LastChatRequest);

        // No name, or a name nothing holds: the PC's own model answers, as before.
        var before = _host.Chat2.LastChatRequest;
        var (s3, _) = await ChatAsync(client, ChatBody(null));
        var (s4, _) = await ChatAsync(client, ChatBody("some-other-name"));
        Assert.Equal(HttpStatusCode.OK, s3);
        Assert.Equal(HttpStatusCode.OK, s4);
        Assert.Same(before, _host.Chat2.LastChatRequest);
    }

    [Fact]
    public async Task AnIdleSecondSlotStopsOnItsOwnAndTheFirstSlotKeepsRunning()
    {
        await LoadPcModelAsync();
        var client = await PairedAsync("Laptop", grant: true);
        await CallAsync(client, HttpMethod.Post, Routes.DeviceModelLoad, Load("alpha"));
        Assert.Single(_remote.Snapshot().LoadedModels);

        // The idle rule stops one slot's engine (here by hand: the supervisor's own timer does it for real).
        await _host.Chat2!.StopAsync(default);
        for (var i = 0; i < 100 && _remote.Snapshot().LoadedModels.Count > 0; i++)
        {
            await Task.Delay(30);
            await _remote.ModelControl.ListAsync(default); // any call reconciles the table
        }

        Assert.Empty(_remote.Snapshot().LoadedModels);
        Assert.Null(_host.Runtime.GetActive(Second));
        Assert.Equal(EngineState.Ready, _host.Chat.State);
        Assert.Equal("pc-model", _host.Runtime.GetActive(ModelKinds.Chat)!.ModelId);
    }

    [Fact]
    public async Task TakeBackStopsTheDeviceSlotOnlyAndLeavesThePcModelAlone()
    {
        await LoadPcModelAsync();
        var client = await PairedAsync("Laptop", grant: true);
        await CallAsync(client, HttpMethod.Post, Routes.DeviceModelLoad, Load("alpha"));

        Assert.True(await _remote.ModelControl.TakeBackAsync(ModelKinds.Chat, default));
        Assert.False(await _remote.ModelControl.TakeBackAsync(ModelKinds.Chat, default));
        Assert.Equal(EngineState.Stopped, _host.Chat2!.State);
        Assert.Null(_host.Runtime.GetActive(Second));
        Assert.Equal(EngineState.Ready, _host.Chat.State);
        Assert.Equal("pc-model", _host.Runtime.GetActive(ModelKinds.Chat)!.ModelId);
        Assert.Empty(_remote.Snapshot().LoadedModels);

        var (status, answer) = await CallAsync(client, HttpMethod.Post, Routes.DeviceModelUnload, """{ "model_id": "alpha" }""");
        AssertError(status, answer, HttpStatusCode.Conflict, "model_conflict", "owner_changed");
    }

    [Fact]
    public async Task TheOwnersActionWinsOverADeviceSlot()
    {
        await LoadPcModelAsync();
        var client = await PairedAsync("Laptop", grant: true);
        await CallAsync(client, HttpMethod.Post, Routes.DeviceModelLoad, Load("alpha"));

        // The owner picks the very model the device holds in the second slot: one model is never in both slots.
        await _remote.ModelControl.OwnerChangedAsync(ModelKinds.Chat, default, "alpha");
        await _host.Runtime.SetActiveAsync(ModelKinds.Chat,
            new ActiveModel(ModelKinds.Chat, "alpha", "alpha", [Path.Combine(_host.RootPath, "models", "alpha.gguf")]), default);
        Assert.Equal(EngineState.Stopped, _host.Chat2!.State);
        Assert.Null(_host.Runtime.GetActive(Second));
        Assert.Empty(_remote.Snapshot().LoadedModels);

        var (status, answer) = await CallAsync(client, HttpMethod.Post, Routes.DeviceModelUnload, """{ "model_id": "alpha" }""");
        AssertError(status, answer, HttpStatusCode.Conflict, "model_conflict", "owner_changed");
    }

    [Fact]
    public async Task AnOwnerChangeWhileTheSecondSlotLoadsEndsTheDevicesLoadWithOwnerChanged()
    {
        await _host.DisposeAsync();
        await _remote.DisposeAsync();
        _http.Dispose();
        await StartAsync(allowReplace: false, chat2: new FakeEngineOptions { LoadDelay = TimeSpan.FromMilliseconds(600) });
        await LoadPcModelAsync();
        var client = await PairedAsync("Laptop", grant: true);

        var load = CallAsync(client, HttpMethod.Post, Routes.DeviceModelLoad, Load("alpha"));
        for (var i = 0; i < 100 && _host.Chat2!.State is EngineState.Stopped; i++)
        {
            await Task.Delay(10);
        }

        await _remote.ModelControl.OwnerChangedAsync(Second, default); // the owner acts on the second slot while it loads
        var (status, answer) = await load;
        AssertError(status, answer, HttpStatusCode.Conflict, "model_conflict", "owner_changed");
        Assert.Equal("pc-model", _host.Runtime.GetActive(ModelKinds.Chat)!.ModelId);
        Assert.Equal(EngineState.Ready, _host.Chat.State);
    }

    [Fact]
    public async Task ANamedModelThatIsInstalledButNotLoadedIsLoadedForADeviceWithTheGrant()
    {
        await LoadPcModelAsync();
        var client = await PairedAsync("Laptop", grant: true);

        var (status, _) = await ChatAsync(client, ChatBody("alpha"));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("alpha", _host.Runtime.GetActive(Second)!.ModelId);
        Assert.Equal(EngineState.Ready, _host.Chat2!.State);
        Assert.NotNull(_host.Chat2.LastChatRequest); // the request was answered by the model it named
        Assert.Equal("pc-model", _host.Chat.Current!.ModelId); // the PC's own model stayed where it was
        Assert.Equal(client.DeviceId, Assert.Single(_remote.Snapshot().LoadedModels).DeviceId);
    }

    [Fact]
    public async Task ANamedModelIsNotLoadedForADeviceWithoutTheGrant()
    {
        await LoadPcModelAsync();
        var client = await PairedAsync("Laptop", grant: false);

        var (status, _) = await ChatAsync(client, ChatBody("alpha"));
        Assert.Equal(HttpStatusCode.OK, status); // answered by the PC's own model, as it always was
        Assert.Null(_host.Runtime.GetActive(Second));
        Assert.Equal(EngineState.Stopped, _host.Chat2!.State);
        Assert.NotNull(_host.Chat.LastChatRequest);
        Assert.Empty(_remote.Snapshot().LoadedModels);
    }

    [Fact]
    public async Task AutoLoadStaysQuietWhenTheConflictRulesRefuseIt()
    {
        await LoadPcModelAsync();
        var first = await PairedAsync("First", grant: true);
        var second = await PairedAsync("Second", grant: true);
        await CallAsync(first, HttpMethod.Post, Routes.DeviceModelLoad, Load("alpha")); // second slot is the first device's

        // The other device's request names "beta": its load is refused (second slot taken, the PC's model protected), so
        // the request is served as it would have been and nothing changes hands.
        var (status, _) = await ChatAsync(second, ChatBody("beta"));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("alpha", _host.Runtime.GetActive(Second)!.ModelId);
        Assert.Equal("pc-model", _host.Runtime.GetActive(ModelKinds.Chat)!.ModelId);

        var (loadStatus, loadBody) = await CallAsync(second, HttpMethod.Post, Routes.DeviceModelLoad, Load("beta"));
        AssertError(loadStatus, loadBody, HttpStatusCode.Conflict, "model_conflict", "in_use_by_device");
    }

    [Fact]
    public async Task WithoutASecondEngineTheOldSingleSlotRulesStillApply()
    {
        // A host that registers no second engine behaves exactly as before: in_use_by_host without "Devices may replace models".
        await using var plain = await TestHost.StartAsync(new FakeEngineOptions { }, withModels: false);
        Assert.False(plain.Runtime.HasSecondSlot(ModelKinds.Chat));
        Assert.True(_host.Runtime.HasSecondSlot(ModelKinds.Chat));
        Assert.Equal(ModelKinds.Chat, plain.Runtime.SlotFor(ModelKinds.Chat, "anything"));
    }

    [Fact]
    public async Task StatusShowsTheSecondSlotOnlyWhileADevicesModelIsInIt()
    {
        await LoadPcModelAsync();
        Assert.DoesNotContain(Second, _host.Runtime.BuildStatus(null).Models.Keys);
        var client = await PairedAsync("Laptop", grant: true);
        await CallAsync(client, HttpMethod.Post, Routes.DeviceModelLoad, Load("alpha"));
        var status = _host.Runtime.BuildStatus(null);
        Assert.Equal("alpha", status.Models[Second].Id);
        Assert.Equal("pc-model", status.Models[ModelKinds.Chat].Id);
        Assert.False(status.Models[Second].KeepLoaded);
    }
}

/// <summary>A device's second model follows the kind's idle time but is never kept loaded.</summary>
public sealed class SecondSlotIdleTests
{
    [Fact(Timeout = 30000)]
    public async Task TheSecondSlotUsesTheKindsIdleTimeAndIgnoresKeepLoaded()
    {
        using var root = new TestRoot();
        await using var shell = await PortalAIHost.Service.HostShell.StartAsync(new PortalAIHost.Service.HostShellOptions
        {
            Paths = PortalAIHost.Service.HostPaths.Resolve(n => n == PortalAIHost.Service.HostPaths.TestRootVariable ? root.Path : null)!,
            Supervisors = new Dictionary<string, IEngineSupervisor>
            {
                [ModelKinds.Chat] = new FakeEngineSupervisor(ModelKinds.Chat),
                [ModelKinds.Embedding] = new FakeEngineSupervisor(ModelKinds.Embedding),
                [ModelSlots.Second(ModelKinds.Chat)] = new FakeEngineSupervisor(ModelSlots.Second(ModelKinds.Chat)),
            },
            KeyProtector = new TestKeyProtector(),
            CallerResolver = new SameUserResolver(),
            QuitGrace = TimeSpan.FromMilliseconds(200),
        }) ?? throw new InvalidOperationException("pipe already owned");

        var second = ModelSlots.Second(ModelKinds.Chat);
        Assert.Equal(shell.IdleUnloadFor(ModelKinds.Chat), shell.IdleUnloadFor(second));
        Assert.NotNull(shell.IdleUnloadFor(second));
    }
}

/// <summary>The planner decides whether a second model fits next to the first; the PC's own limits always win.</summary>
public sealed class SecondSlotMemoryTests
{
    private const long GiB = 1L << 30;

    private sealed class SmallPc : IHardwareProbe
    {
        public Task<HardwareProfile> ProbeAsync(CancellationToken ct) => Task.FromResult(
            new HardwareProfile("windows", "x64", 12, 32 * GiB, [], [], "fp"));

        public MemorySnapshot GetMemory() => new(32 * GiB, 32 * GiB, []);

        public bool IsOnBattery => false;
    }

    private static CatalogueEntry Entry(string id, long weights) => new()
    {
        Id = id, DisplayName = id, Kind = ModelKinds.Chat, Quality = 3, HfRepo = "test/" + id,
        HfCommit = new string('a', 40), ContextMax = 4096, ContextDefault = 2048,
        Memory = new ModelMemory { WeightsBytes = weights, NonExpertBytes = weights, NLayers = 24, KvBytesPer1kCtx = 16L << 20, ComputeOverheadBytes = 128L << 20 },
        Files = [new CatalogueFile { Name = id + ".gguf", Size = 10, Sha256 = new string('b', 64), Urls = ["https://example.invalid/" + id] }],
    };

    private sealed class Host(Dictionary<string, CatalogueEntry> entries) : IModelControlHost
    {
        public Task<IReadOnlyList<InstalledModel>> ListInstalledAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<InstalledModel>>([.. entries.Values.Select(e => new InstalledModel(e.Id, e.DisplayName, e.Kind, 1, true))]);

        public Task<ActiveModel?> ResolveAsync(string modelId, CancellationToken ct) =>
            Task.FromResult<ActiveModel?>(entries.TryGetValue(modelId, out var e)
                ? new ActiveModel(e.Kind, e.Id, e.DisplayName, [e.Id + ".gguf"], e)
                : null);

        public Task ChangedAsync() => Task.CompletedTask;
    }

    [Fact]
    public async Task ASecondLoadIsRefusedWhenBothDoNotFitAndNothingIsEvicted()
    {
        using var root = new TestRoot();
        var entries = new[] { Entry("pc-model", 3 * GiB), Entry("small", 3 * GiB), Entry("huge", 23 * GiB) }.ToDictionary(e => e.Id);
        var chat = new FakeEngineSupervisor(ModelKinds.Chat);
        var chat2 = new FakeEngineSupervisor(ModelSlots.Second(ModelKinds.Chat));
        await using var runtime = await AiHostRuntime.StartAsync(new AiHostRuntimeOptions
        {
            Registry = new Core.Auth.ClientRegistry(new() { DataDirectory = root.Path }, new TestKeyProtector()),
            Scheduler = new FairScheduler(),
            Supervisors = new Dictionary<string, IEngineSupervisor>
            {
                [ModelKinds.Chat] = chat,
                [ModelKinds.Embedding] = new FakeEngineSupervisor(ModelKinds.Embedding),
                [ModelSlots.Second(ModelKinds.Chat)] = chat2,
            },
            Hardware = new SmallPc(),
        });
        var host = new Host(entries);
        await runtime.SetActiveAsync(ModelKinds.Chat, (await host.ResolveAsync("pc-model", default))!, default);
        await runtime.EnsureLoadedAsync(ModelKinds.Chat, default);

        var devices = new DeviceStore(root.Path);
        var device = devices.Add("Laptop", [1, 2, 3]);
        Assert.True(devices.SetModelControl(device.Id, true));
        var control = new ModelControl(runtime, devices, host, () => false);

        // 3 GiB next to 3 GiB fits in what this PC leaves for models.
        var small = await control.LoadAsync(device.Id, new DeviceLoadRequest { ModelId = "small" }, default);
        Assert.Equal("loaded", small.State);
        Assert.Equal(EngineState.Ready, chat.State);
        Assert.Equal(EngineState.Ready, chat2.State);

        // 23 GiB does not fit next to the first slot's model: refused, and the second slot still holds what it held.
        var refused = await Assert.ThrowsAsync<ApiException>(() => control.LoadAsync(device.Id, new DeviceLoadRequest { ModelId = "huge" }, default));
        Assert.Equal(ErrorCodes.InsufficientMemory, refused.Code);
        Assert.Equal("small", runtime.GetActive(ModelSlots.Second(ModelKinds.Chat))!.ModelId);
        Assert.Equal(EngineState.Ready, chat2.State);
        Assert.Equal(EngineState.Ready, chat.State);
        Assert.Equal("small", Assert.Single(control.Loaded()).ModelId);

        // The same 23 GiB model fits once the first slot's engine is gone: the planner counts what is really running.
        await runtime.UnloadAsync(ModelKinds.Chat, default);
        await control.TakeBackAsync(ModelKinds.Chat, default);
        var huge = await control.LoadAsync(device.Id, new DeviceLoadRequest { ModelId = "huge" }, default);
        Assert.Equal("loaded", huge.State);
    }

    [Fact]
    public async Task ThePcsOwnModelReloadingTakesTheMemoryBackFromTheDeviceSlot()
    {
        using var root = new TestRoot();
        var entries = new[] { Entry("pc-model", 16 * GiB), Entry("dev", 12 * GiB) }.ToDictionary(e => e.Id);
        var chat = new FakeEngineSupervisor(ModelKinds.Chat);
        var chat2 = new FakeEngineSupervisor(ModelSlots.Second(ModelKinds.Chat));
        await using var runtime = await AiHostRuntime.StartAsync(new AiHostRuntimeOptions
        {
            Registry = new Core.Auth.ClientRegistry(new() { DataDirectory = root.Path }, new TestKeyProtector()),
            Scheduler = new FairScheduler(),
            Supervisors = new Dictionary<string, IEngineSupervisor>
            {
                [ModelKinds.Chat] = chat,
                [ModelKinds.Embedding] = new FakeEngineSupervisor(ModelKinds.Embedding),
                [ModelSlots.Second(ModelKinds.Chat)] = chat2,
            },
            Hardware = new SmallPc(),
        });
        var host = new Host(entries);
        await runtime.SetActiveAsync(ModelKinds.Chat, (await host.ResolveAsync("pc-model", default))!, default);

        var devices = new DeviceStore(root.Path);
        var device = devices.Add("Laptop", [1, 2, 3]);
        Assert.True(devices.SetModelControl(device.Id, true));
        var control = new ModelControl(runtime, devices, host, () => false);

        // The PC's model is not loaded yet (idle): the device's model takes the second engine and runs.
        await control.LoadAsync(device.Id, new DeviceLoadRequest { ModelId = "dev" }, default);
        Assert.Equal(EngineState.Ready, chat2.State);

        // Now the PC's own app needs its model: 16 GiB does not fit next to 12 GiB, so the PC's limits win and the device's engine stops.
        await runtime.EnsureLoadedAsync(ModelKinds.Chat, default);
        Assert.Equal(EngineState.Ready, chat.State);
        Assert.Equal(EngineState.Stopped, chat2.State);
    }
}
