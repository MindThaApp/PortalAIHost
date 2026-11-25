using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Remote;
using Portal.AiHost.Core.Remote.Client;
using Portal.AiHost.Core.Runtime;
using Portal.AiHost.Core.Tests.Remote;
using Portal.AiHost.FakeEngine;
using Portal.AiHost.Server;

namespace Portal.AiHost.Server.Tests;

/// <summary>
/// Model control from the laptop side (docs/client-mode-v1.md 4.7, step 8): <see cref="RemoteModelClient"/> over a real
/// <see cref="RemoteHostLink"/> against the real loopback LAN listener of a PC with a paired, granted device.
/// </summary>
public sealed class LaptopModelControlTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private RemoteAccess _remote = null!;
    private FakeModelHost _models = null!;
    private readonly string _stateRoot = Path.Combine(Path.GetTempPath(), "pah-laptop-models-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _links = [];

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.StartAsync(new FakeEngineOptions { }, withModels: false);
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
        await _remote.ApplyAsync(new RemoteSettings { Enabled = true, Relay = false, AllowReplaceModels = true });
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var link in _links)
        {
            link.Dispose();
        }

        await _remote.DisposeAsync();
        await _host.DisposeAsync();
        if (Directory.Exists(_stateRoot)) // Test-owned temp folder.
        {
            Directory.Delete(_stateRoot, recursive: true);
        }
    }

    private sealed record Laptop(RemoteHostLink Link, RemoteModelClient Models, string DeviceId);

    private async Task<Laptop> PairLaptopAsync(string name, bool grant)
    {
        var store = new ClientStateStore(Path.Combine(_stateRoot, name), new XorSecretProtector());
        var offer = _remote.StartPairing();
        var text = RemoteNetwork.QrText("127.0.0.1", _remote.Port, offer.Fingerprint, PairingCrypto.Normalize(offer.Code)!, "Desk PC");
        Assert.True(PairingLink.TryParse(text, out var parsed));
        var outcome = await new DevicePairingClient(new LanConnector(), store) { AllowLoopback = true }
            .PairAsync(PairingTarget.FromLink(parsed!, name));
        Assert.True(outcome.Succeeded, outcome.Message);
        var link = new RemoteHostLink(store, new LanConnector(), options: new RemoteHostLinkOptions { WatchNetwork = false });
        _links.Add(link);
        var asking = link.RequestTokenAsync();
        await Wait.UntilAsync(() => _host.Registry.PendingRequests.Count > 0, "a token request on the PC");
        await _host.Registry.ApproveAsync(_host.Registry.PendingRequests[0].RequestId, default);
        Assert.Equal(TokenOutcome.Approved, await asking);
        var deviceId = store.LoadRemote()!.DeviceId;
        if (grant)
        {
            Assert.True(_remote.SetModelControl(deviceId, true));
        }

        return new Laptop(link, new RemoteModelClient(link), deviceId);
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50);
        }

        Assert.True(condition());
    }

    [Fact]
    public async Task List_load_and_unload_work_with_the_grant()
    {
        var laptop = await PairLaptopAsync("Laptop", grant: true);
        var before = _models.Tree();

        var list = await laptop.Models.ListAsync();
        Assert.True(list.Ok);
        Assert.True(list.ModelControl);
        Assert.Equal("Desk PC", list.PcName);
        Assert.Equal(["alpha", "beta", "pc-model"], list.Models.Select(m => m.Id).Order().ToArray());
        Assert.True(laptop.Models.Granted);

        var loaded = await laptop.Models.LoadAsync("alpha", DevicePlacements.Cpu);
        Assert.True(loaded.Ok, loaded.Message);
        var alpha = loaded.Models.Single(m => m.Id == "alpha");
        Assert.Equal("loaded", alpha.State);
        Assert.Equal(laptop.DeviceId, alpha.LoadedBy);
        Assert.Equal(DevicePlacements.Cpu, alpha.Placement);
        Assert.Equal(["alpha"], laptop.Models.LoadedByThisLaptop.ToArray());
        Assert.Equal("alpha", _host.Runtime.GetActive(ModelKinds.Chat)!.ModelId);

        var unloaded = await laptop.Models.UnloadAsync("alpha");
        Assert.True(unloaded.Ok, unloaded.Message);
        Assert.Empty(laptop.Models.LoadedByThisLaptop);
        Assert.Equal("pc-model", _host.Runtime.GetActive(ModelKinds.Chat)!.ModelId);
        Assert.Equal(before, _models.Tree()); // No model file was created, changed or deleted.
    }

    [Fact]
    public async Task Without_the_grant_every_call_is_a_plain_refusal()
    {
        var laptop = await PairLaptopAsync("Laptop", grant: false);
        var before = _models.Tree();

        var answers = new[]
        {
            await laptop.Models.ListAsync(),
            await laptop.Models.LoadAsync("alpha", null),
            await laptop.Models.UnloadAsync("alpha"),
            await laptop.Models.GetPlacementsAsync(),
            await laptop.Models.SetPlacementAsync(new DevicePlacementRequest { ModelId = "alpha", Compute = DevicePlacements.Cpu }),
        };

        Assert.All(answers, a =>
        {
            Assert.False(a.Ok);
            Assert.False(a.ModelControl);
            Assert.Equal(ErrorCodes.ModelControlRequired, a.ErrorCode);
            Assert.Equal("This laptop does not have Model control. Ask the PC owner.", a.Message);
        });
        Assert.False(laptop.Models.Granted);
        Assert.Equal(before, _models.Tree());
    }

    [Fact]
    public async Task Turning_the_grant_off_mid_use_is_noticed_on_the_next_call()
    {
        var laptop = await PairLaptopAsync("Laptop", grant: true);
        Assert.True((await laptop.Models.LoadAsync("alpha", null)).Ok);
        Assert.Equal(["alpha"], laptop.Models.LoadedByThisLaptop.ToArray());

        _remote.SetModelControl(laptop.DeviceId, false);
        var after = await laptop.Models.ListAsync();

        Assert.False(after.Ok);
        Assert.Equal(ErrorCodes.ModelControlRequired, after.ErrorCode);
        Assert.False(laptop.Models.Granted);
        Assert.Empty(laptop.Models.LoadedByThisLaptop);
    }

    [Fact]
    public async Task Owner_changed_is_mapped_and_the_list_is_refreshed()
    {
        var laptop = await PairLaptopAsync("Laptop", grant: true);
        await laptop.Models.LoadAsync("alpha", null);
        await _remote.ModelControl.OwnerChangedAsync(ModelKinds.Chat, default);
        await _host.Runtime.SetActiveAsync(ModelKinds.Chat,
            new ActiveModel(ModelKinds.Chat, "beta", "beta", [Path.Combine(_host.RootPath, "models", "beta.gguf")]), default);

        var answer = await laptop.Models.UnloadAsync("alpha");

        Assert.False(answer.Ok);
        Assert.Equal(ErrorCodes.ModelConflict, answer.ErrorCode);
        Assert.Equal(ModelConflictReasons.OwnerChanged, answer.Reason);
        Assert.Equal("The PC owner changed the model. The list is refreshed.", answer.Message);
        Assert.NotEmpty(answer.Models);
        Assert.DoesNotContain(answer.Models, m => m.LoadedBy == laptop.DeviceId);
        Assert.Empty(laptop.Models.LoadedByThisLaptop);
    }

    [Fact]
    public async Task In_use_by_the_pcs_own_apps_is_mapped_unless_replacing_is_allowed()
    {
        var laptop = await PairLaptopAsync("Laptop", grant: true);
        await _remote.ApplyAsync(new RemoteSettings { Enabled = true, Relay = false, AllowReplaceModels = false });
        await _host.Runtime.EnsureLoadedAsync(ModelKinds.Chat, default);

        var answer = await laptop.Models.LoadAsync("alpha", null);

        Assert.False(answer.Ok);
        Assert.Equal(ModelConflictReasons.InUseByHost, answer.Reason);
        Assert.Equal("The PC's own apps are using the model. Ask the PC owner to allow replacing models.", answer.Message);
        Assert.Equal("pc-model", _host.Runtime.GetActive(ModelKinds.Chat)!.ModelId);
    }

    [Fact]
    public async Task In_use_by_another_device_and_not_loaded_by_this_laptop_are_mapped()
    {
        var first = await PairLaptopAsync("Laptop one", grant: true);
        var second = await PairLaptopAsync("Laptop two", grant: true);
        Assert.True((await first.Models.LoadAsync("alpha", null)).Ok);

        var taken = await second.Models.LoadAsync("beta", null);
        Assert.False(taken.Ok);
        Assert.Equal(ModelConflictReasons.InUseByDevice, taken.Reason);
        Assert.Equal("Another device is using the model.", taken.Message);

        var notMine = await second.Models.UnloadAsync("alpha");
        Assert.False(notMine.Ok);
        Assert.Equal(ModelConflictReasons.NotLoadedByDevice, notMine.Reason);
        Assert.Equal("This laptop did not load that model, so it cannot unload it.", notMine.Message);
        Assert.Equal(["alpha"], first.Models.LoadedByThisLaptop.ToArray());
    }

    [Fact]
    public async Task Unknown_model_and_bad_placement_get_short_messages()
    {
        var laptop = await PairLaptopAsync("Laptop", grant: true);

        var missing = await laptop.Models.LoadAsync("no-such-model", null);
        Assert.False(missing.Ok);
        Assert.Equal(ErrorCodes.NotFound, missing.ErrorCode);
        Assert.Equal("The PC does not have that model.", missing.Message);

        var bad = await laptop.Models.LoadAsync("alpha", "tape");
        Assert.False(bad.Ok);
        Assert.Equal("That placement is not available.", bad.Message);
    }

    [Fact]
    public async Task Placement_can_be_read_and_set_for_a_model_this_laptop_loaded()
    {
        var laptop = await PairLaptopAsync("Laptop", grant: true);
        await laptop.Models.LoadAsync("alpha", null);

        var set = await laptop.Models.SetPlacementAsync(new DevicePlacementRequest
        {
            ModelId = "alpha", Compute = DevicePlacements.Cpu, CpuThreads = 2,
        });

        Assert.True(set.Ok, set.Message);
        var placement = Assert.Single(set.Placements!);
        Assert.Equal("alpha", placement.ModelId);
        Assert.Equal(DevicePlacements.Cpu, placement.Compute);
        var read = await laptop.Models.GetPlacementsAsync();
        Assert.Equal(DevicePlacements.Cpu, Assert.Single(read.Placements!).Compute);
    }

    [Fact]
    public async Task Bursts_of_list_calls_share_one_request_to_the_pc()
    {
        var laptop = await PairLaptopAsync("Laptop", grant: true);
        var tasks = Enumerable.Range(0, 8).Select(_ => laptop.Models.ListAsync()).ToArray();
        var answers = await Task.WhenAll(tasks);

        Assert.All(answers, a => Assert.True(a.Ok));
        Assert.True(answers.Distinct().Count() < 8, "calls made together share one answer");
    }

    [Fact]
    public async Task Cancelling_a_list_call_returns_to_the_caller_at_once()
    {
        var laptop = await PairLaptopAsync("Laptop", grant: true);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => laptop.Models.ListAsync(cts.Token));
        Assert.True((await laptop.Models.ListAsync()).Ok); // The shared request itself carried on and the client still works.
    }

    [Fact]
    public async Task Plain_messages_cover_every_reason()
    {
        Assert.Equal("The PC cannot be reached right now.", RemoteModelClient.PlainMessage("remote_unreachable", null));
        Assert.Equal("The PC does not have enough free memory for that model.",
            RemoteModelClient.PlainMessage(ErrorCodes.InsufficientMemory, null));
        Assert.Equal("The PC is not accepting requests from this laptop right now.",
            RemoteModelClient.PlainMessage("device_paused", null));
        Assert.Equal("The model is busy on the PC.", RemoteModelClient.PlainMessage(ErrorCodes.ModelConflict, "something_new"));
        Assert.Equal("The PC could not do that.", RemoteModelClient.PlainMessage("weird", null));
        await Task.CompletedTask;
    }
}

/// <summary>The per-app PC model (<c>client\apps.json</c>) and its use on forwarded requests.</summary>
public sealed class LaptopAppModelTests : IAsyncLifetime
{
    private const string App = "com.mindthaapp.voiceportal";
    private const string Chat = """{ "messages": [ { "role": "user", "content": "Hi" } ], "temperature": 0.5 }""";

    private readonly string _stateRoot = Path.Combine(Path.GetTempPath(), "pah-appmodel-" + Guid.NewGuid().ToString("N"));
    private readonly ScriptedRemote _pc = new();
    private TestHost _laptop = null!;
    private RemoteAccess _remote = null!;
    private ClientModeController _mode = null!;
    private AppModelStore _maps = null!;
    private string _token = null!;
    private volatile string _modelControl = "true";

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
        _maps = new AppModelStore(store.Directory);
        _pc.OnOpen = (request, _) => Task.FromResult(request.Target == Routes.Capabilities
            ? ScriptedRemote.Json(200, $$"""{"api_version":"1","capabilities_revision":1,"chat":{"available":true},"model_control":{{_modelControl}}}""")
            : ScriptedRemote.Json(200, """{"id":"chatcmpl-1"}"""));
        _mode = new ClientModeController(_laptop.Runtime, _remote, new ClientModeOptions
        {
            CreateLink = () => new RemoteHostLink(new ClientStateStore(_stateRoot, new XorSecretProtector()), _pc,
                options: new RemoteHostLinkOptions { WatchNetwork = false }),
            DrainGrace = TimeSpan.FromSeconds(2),
            AppModels = _maps,
            Backend = new RemoteBackendOptions { CapabilitiesCache = TimeSpan.Zero },
        });
        Assert.Null((await _mode.SetModeAsync(HostModes.Client)).Refused);
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

    private async Task AskAsync(string path, string json)
    {
        using var request = _laptop.Authed(HttpMethod.Post, path, _token, json);
        using var response = await _laptop.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task SeeCapabilitiesAsync()
    {
        using var request = _laptop.Authed(HttpMethod.Get, Routes.Capabilities, _token, null);
        using var response = await _laptop.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private string LastBodyOf(string target)
    {
        lock (_pc.Requests)
        {
            return Encoding.UTF8.GetString(_pc.Requests.Last(r => r.Target == target).Body!);
        }
    }

    [Fact]
    public async Task The_mapped_model_replaces_model_on_chat_and_embeddings_with_the_grant()
    {
        await SeeCapabilitiesAsync();
        Assert.True(_mode.Models!.Granted);
        Assert.True(_maps.Set(App, "alpha"));

        await AskAsync(Routes.ChatCompletions, Chat);
        var sent = JsonDocument.Parse(LastBodyOf(Routes.ChatCompletions)).RootElement;
        Assert.Equal("alpha", sent.GetProperty("model").GetString());
        Assert.Equal(0.5, sent.GetProperty("temperature").GetDouble()); // Everything else is left as the app sent it.
        Assert.Equal("Hi", sent.GetProperty("messages")[0].GetProperty("content").GetString());

        await AskAsync(Routes.Embeddings, """{ "input": "x", "model": "app-chose-this" }""");
        Assert.Equal("alpha", JsonDocument.Parse(LastBodyOf(Routes.Embeddings)).RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Without_a_choice_or_without_the_grant_model_is_left_as_the_app_sent_it()
    {
        await SeeCapabilitiesAsync();
        await AskAsync(Routes.ChatCompletions, Chat);
        Assert.Equal(Chat, LastBodyOf(Routes.ChatCompletions)); // PC's choice: untouched.

        _maps.Set(App, "alpha");
        _modelControl = "false";
        await SeeCapabilitiesAsync();
        Assert.False(_mode.Models!.Granted);

        await AskAsync(Routes.ChatCompletions, Chat);
        Assert.Equal(Chat, LastBodyOf(Routes.ChatCompletions)); // Grant revoked: the choice is hidden and not applied.
        Assert.Equal("alpha", _maps.Get(App)); // ... but it is kept for when the grant comes back.

        _modelControl = "true";
        await SeeCapabilitiesAsync();
        await AskAsync(Routes.ChatCompletions, Chat);
        Assert.Contains("\"alpha\"", LastBodyOf(Routes.ChatCompletions));
    }

    [Fact]
    public async Task Status_carries_the_grant()
    {
        Assert.Null(_mode.Snapshot().ModelControl);
        await SeeCapabilitiesAsync();
        Assert.True(_mode.Snapshot().ModelControl);
        Assert.Empty(_mode.Snapshot().LoadedOnPc!);
    }

    [Fact]
    public void Choices_are_saved_atomically_and_survive_a_restart()
    {
        var dir = Path.Combine(_stateRoot, "maps");
        var store = new AppModelStore(dir);
        Assert.True(store.Set("app.one", "alpha"));
        Assert.True(store.Set("app.two", "beta"));
        Assert.True(store.Set("app.one", null)); // Clears (rewrites the file without it).

        var again = new AppModelStore(dir);
        Assert.Null(again.Get("app.one"));
        Assert.Equal("beta", again.Get("app.two"));
        Assert.Equal([AppModelStore.FileName], Directory.GetFiles(dir).Select(p => Path.GetFileName(p)!).ToArray()); // No temp file left.
        Assert.False(store.Set("", "alpha"));
        Assert.False(store.Set("app.three", "bad\nmodel"));
        Assert.False(store.Set("app.three", new string('x', 500)));
    }

    [Fact]
    public void A_damaged_file_reads_as_empty_and_is_not_touched_until_a_choice_is_saved()
    {
        var dir = Path.Combine(_stateRoot, "damaged");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, AppModelStore.FileName);
        File.WriteAllText(path, "{ not json");

        var store = new AppModelStore(dir);

        Assert.Empty(store.All());
        Assert.Equal("{ not json", File.ReadAllText(path));
    }

    [Fact]
    public void WithModel_only_touches_json_objects()
    {
        Assert.Equal("""{"a":1,"model":"m"}""", Encoding.UTF8.GetString(RemoteBackend.WithModel("""{"a":1}"""u8.ToArray(), "m")));
        Assert.Equal("""[1]""", Encoding.UTF8.GetString(RemoteBackend.WithModel("""[1]"""u8.ToArray(), "m")));
        Assert.Equal("nope", Encoding.UTF8.GetString(RemoteBackend.WithModel("nope"u8.ToArray(), "m")));
    }
}
