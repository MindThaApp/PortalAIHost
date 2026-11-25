using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Remote;
using Portal.AiHost.Core.Runtime;
using Portal.AiHost.Core.Tests.Remote;
using Portal.AiHost.FakeEngine;
using Portal.AiHost.Server;

namespace Portal.AiHost.Server.Tests;

/// <summary>Model files on disk and what the fake store says about them. Nothing here is ever written by a device call.</summary>
internal sealed class FakeModelHost : IModelControlHost
{
    private readonly string _folder;
    public int Changes;

    public FakeModelHost(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(folder);
        foreach (var id in new[] { "pc-model", "alpha", "beta" })
        {
            File.WriteAllText(Path.Combine(folder, id + ".gguf"), "model bytes of " + id);
        }
    }

    public Task<IReadOnlyList<InstalledModel>> ListInstalledAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<InstalledModel>>(
        [
            new("pc-model", "PC model", ModelKinds.Chat, 100, true),
            new("alpha", "Alpha", ModelKinds.Chat, 200, true),
            new("beta", "Beta", ModelKinds.Chat, 300, true),
        ]);

    public Task<ActiveModel?> ResolveAsync(string modelId, CancellationToken ct) =>
        Task.FromResult<ActiveModel?>(modelId is "pc-model" or "alpha" or "beta"
            ? new ActiveModel(ModelKinds.Chat, modelId, modelId, [Path.Combine(_folder, modelId + ".gguf")])
            : null);

    public Task ChangedAsync()
    {
        Interlocked.Increment(ref Changes);
        return Task.CompletedTask;
    }

    /// <summary>Path, size and hash of every file, to compare before and after.</summary>
    public string Tree() => string.Join('|', Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories).Order()
        .Select(f => $"{f}:{new FileInfo(f).Length}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))}"));
}

/// <summary>Model control for paired devices over real TLS on loopback, with signed requests (docs/client-mode-v1.md 4.7).</summary>
public sealed class DeviceModelControlTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private RemoteAccess _remote = null!;
    private HttpClient _http = null!;
    private FakeModelHost _models = null!;
    private readonly List<PairingClient> _clients = [];

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
        await _remote.ApplyAsync(new RemoteSettings { Enabled = true, Relay = false });
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

    private async Task<PairingClient> PairAsync(string name = "Laptop")
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
        _clients.Add(client);
        return client;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> CallAsync(PairingClient client, HttpMethod method, string target,
        string? json = null)
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

        using var response = await _http.SendAsync(message);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

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

    private static string Load(string id, string? placement = null) =>
        placement is null ? $$"""{ "model_id": "{{id}}" }""" : $$"""{ "model_id": "{{id}}", "placement": "{{placement}}" }""";

    private static string Unload(string id) => $$"""{ "model_id": "{{id}}" }""";

    private async Task<PairingClient> PairedWithGrantAsync(string name = "Laptop")
    {
        var client = await PairAsync(name);
        Assert.True(_remote.SetModelControl(client.DeviceId!, true));
        return client;
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
    public async Task WithoutTheGrantEveryRouteIsRefused()
    {
        var client = await PairAsync();
        var before = _models.Tree();
        foreach (var (method, target, body) in new (HttpMethod, string, string?)[]
        {
            (HttpMethod.Get, Routes.DeviceModels, null),
            (HttpMethod.Post, Routes.DeviceModelLoad, Load("alpha")),
            (HttpMethod.Post, Routes.DeviceModelUnload, Unload("alpha")),
            (HttpMethod.Get, Routes.DevicePlacement, null),
            (HttpMethod.Post, Routes.DevicePlacement, """{ "model_id": "alpha", "compute": "cpu" }"""),
        })
        {
            var (status, answer) = await CallAsync(client, method, target, body);
            AssertError(status, answer, HttpStatusCode.Forbidden, "model_control_required");
        }

        Assert.Equal(EngineState.Stopped, _host.Chat.State);
        Assert.Equal("pc-model", _host.Runtime.GetActive(ModelKinds.Chat)!.ModelId);
        Assert.Equal(before, _models.Tree());
    }

    [Fact]
    public async Task ARequestWithoutASignatureOrFromThisPcFindsNoModelRoute()
    {
        using var unsigned = await _http.GetAsync(Routes.DeviceModels);
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);

        // The loopback listener (apps on this PC) has no device: the routes do not exist there.
        var token = await _host.GetTokenAsync();
        using var local = await _host.Http.SendAsync(_host.Authed(HttpMethod.Get, Routes.DeviceModels, token));
        Assert.Equal(HttpStatusCode.NotFound, local.StatusCode);
    }

    [Fact]
    public async Task RoutesOutsideTheListStayRefusedEvenWithTheGrant()
    {
        var client = await PairedWithGrantAsync();
        foreach (var (method, target) in new[]
        {
            (HttpMethod.Post, "/host/models/remove"), (HttpMethod.Post, "/host/models/download"), (HttpMethod.Delete, "/host/models/alpha"),
            (HttpMethod.Post, "/host/settings"), (HttpMethod.Post, "/host/models/verify"), (HttpMethod.Post, "/host/models/import"),
        })
        {
            var (status, answer) = await CallAsync(client, method, target, method == HttpMethod.Post ? "{}" : null);
            AssertError(status, answer, HttpStatusCode.NotFound, "not_found");
        }
    }

    [Fact]
    public async Task WithTheGrantAModelLoadsAndUnloadsAndNoFileChanges()
    {
        var client = await PairedWithGrantAsync();
        var before = _models.Tree();

        var (listStatus, list) = await CallAsync(client, HttpMethod.Get, Routes.DeviceModels);
        Assert.Equal(HttpStatusCode.OK, listStatus);
        Assert.Equal(3, list.GetProperty("models").GetArrayLength());

        var (status, loaded) = await CallAsync(client, HttpMethod.Post, Routes.DeviceModelLoad, Load("alpha"));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("loaded", loaded.GetProperty("state").GetString());
        Assert.Equal(client.DeviceId, loaded.GetProperty("loaded_by").GetString());
        Assert.Equal(EngineState.Ready, _host.Chat.State);
        Assert.Equal("alpha", _host.Runtime.GetActive(ModelKinds.Chat)!.ModelId);
        var info = Assert.Single(_remote.Snapshot().LoadedModels);
        Assert.Equal((client.DeviceId, "Laptop", "alpha"), (info.DeviceId, info.DeviceName, info.ModelId));

        var (unStatus, gone) = await CallAsync(client, HttpMethod.Post, Routes.DeviceModelUnload, Unload("alpha"));
        Assert.Equal(HttpStatusCode.OK, unStatus);
        Assert.Equal("unloaded", gone.GetProperty("state").GetString());
        Assert.Equal(EngineState.Stopped, _host.Chat.State);
        Assert.Equal("pc-model", _host.Runtime.GetActive(ModelKinds.Chat)!.ModelId); // the PC's own choice is back
        Assert.Empty(_remote.Snapshot().LoadedModels);
        Assert.Equal(before, _models.Tree()); // unload is memory only
    }

    [Fact]
    public async Task TheCapabilitiesAnswerCarriesTheGrantOnlyForADevice()
    {
        var client = await PairAsync();
        Assert.False(_host.Runtime.BuildCapabilities(client.DeviceId).ModelControl);
        _remote.SetModelControl(client.DeviceId!, true);
        Assert.True(_host.Runtime.BuildCapabilities(client.DeviceId).ModelControl);
        Assert.Null(_host.Runtime.BuildCapabilities().ModelControl);
    }

    [Fact]
    public async Task RevokingMidWayKeepsTheLoadedModelUntilItGoesIdle()
    {
        var client = await PairedWithGrantAsync();
        await CallAsync(client, HttpMethod.Post, Routes.DeviceModelLoad, Load("alpha"));

        _remote.SetModelControl(client.DeviceId!, false);
        var (status, answer) = await CallAsync(client, HttpMethod.Post, Routes.DeviceModelUnload, Unload("alpha"));
        AssertError(status, answer, HttpStatusCode.Forbidden, "model_control_required");
        Assert.Equal(EngineState.Ready, _host.Chat.State); // still loaded
        Assert.Equal("alpha", _host.Runtime.GetActive(ModelKinds.Chat)!.ModelId);
        Assert.Single(_remote.Snapshot().LoadedModels);

        // The normal idle unload stops the engine: the device's claim ends and the PC's choice returns.
        await _host.Runtime.UnloadAsync(ModelKinds.Chat, default);
        await EventuallyAsync(() => _remote.Snapshot().LoadedModels.Count == 0);
        await EventuallyAsync(() => _host.Runtime.GetActive(ModelKinds.Chat)!.ModelId == "pc-model");
    }

    [Fact]
    public async Task TheOwnersLocalActionWinsOverADeviceRequest()
    {
        var client = await PairedWithGrantAsync();
        await CallAsync(client, HttpMethod.Post, Routes.DeviceModelLoad, Load("alpha"));

        // The owner picks another model on this PC, as the Models page does.
        await _remote.ModelControl.OwnerChangedAsync(ModelKinds.Chat, default);
        await _host.Runtime.SetActiveAsync(ModelKinds.Chat,
            new ActiveModel(ModelKinds.Chat, "beta", "beta", [Path.Combine(_host.RootPath, "models", "beta.gguf")]), default);

        Assert.Equal("beta", _host.Runtime.GetActive(ModelKinds.Chat)!.ModelId);
        Assert.Equal(EngineState.Stopped, _host.Chat.State);
        var (status, answer) = await CallAsync(client, HttpMethod.Post, Routes.DeviceModelUnload, Unload("alpha"));
        AssertError(status, answer, HttpStatusCode.Conflict, "model_conflict", "owner_changed");
        Assert.Equal("beta", _host.Runtime.GetActive(ModelKinds.Chat)!.ModelId); // the device's call undid nothing
        Assert.Empty(_remote.Snapshot().LoadedModels);
    }

    [Fact]
    public async Task ADeviceCannotReplaceAModelThePcIsUsingUnlessTheSwitchIsOn()
    {
        var client = await PairedWithGrantAsync();
        await _host.Runtime.EnsureLoadedAsync(ModelKinds.Chat, default); // the PC's own apps use pc-model
        Assert.Equal(EngineState.Ready, _host.Chat.State);

        var (status, answer) = await CallAsync(client, HttpMethod.Post, Routes.DeviceModelLoad, Load("alpha"));
        AssertError(status, answer, HttpStatusCode.Conflict, "model_conflict", "in_use_by_host");
        Assert.Equal("pc-model", _host.Runtime.GetActive(ModelKinds.Chat)!.ModelId);
        Assert.Equal(EngineState.Ready, _host.Chat.State);

        await _remote.ApplyAsync(new RemoteSettings { Enabled = true, Relay = false, AllowReplaceModels = true });
        var (okStatus, loaded) = await CallAsync(client, HttpMethod.Post, Routes.DeviceModelLoad, Load("alpha"));
        Assert.Equal(HttpStatusCode.OK, okStatus);
        Assert.Equal("alpha", loaded.GetProperty("id").GetString());
        Assert.Equal("alpha", _host.Runtime.GetActive(ModelKinds.Chat)!.ModelId);

        // Giving it back: the PC's own model is active again (it loads on the next request).
        await CallAsync(client, HttpMethod.Post, Routes.DeviceModelUnload, Unload("alpha"));
        Assert.Equal("pc-model", _host.Runtime.GetActive(ModelKinds.Chat)!.ModelId);
    }

    [Fact]
    public async Task ADeviceCannotUnloadAModelThePcLoaded()
    {
        var client = await PairedWithGrantAsync();
        await _host.Runtime.EnsureLoadedAsync(ModelKinds.Chat, default);
        var (status, answer) = await CallAsync(client, HttpMethod.Post, Routes.DeviceModelUnload, Unload("pc-model"));
        AssertError(status, answer, HttpStatusCode.Conflict, "model_conflict", "not_loaded_by_device");
        Assert.Equal(EngineState.Ready, _host.Chat.State);

        // Loading the very model the PC already runs changes nothing and claims nothing.
        var (okStatus, same) = await CallAsync(client, HttpMethod.Post, Routes.DeviceModelLoad, Load("pc-model"));
        Assert.Equal(HttpStatusCode.OK, okStatus);
        Assert.Equal("pc", same.GetProperty("loaded_by").GetString());
        Assert.Empty(_remote.Snapshot().LoadedModels);
    }

    [Fact]
    public async Task TheFirstDeviceKeepsItsModelAgainstASecondDevice()
    {
        var first = await PairedWithGrantAsync("Laptop one");
        var second = await PairedWithGrantAsync("Laptop two");
        await CallAsync(first, HttpMethod.Post, Routes.DeviceModelLoad, Load("alpha"));

        var (status, answer) = await CallAsync(second, HttpMethod.Post, Routes.DeviceModelLoad, Load("beta"));
        AssertError(status, answer, HttpStatusCode.Conflict, "model_conflict", "in_use_by_device");
        Assert.Equal("alpha", _host.Runtime.GetActive(ModelKinds.Chat)!.ModelId);

        var (notYours, notYoursBody) = await CallAsync(second, HttpMethod.Post, Routes.DeviceModelUnload, Unload("alpha"));
        AssertError(notYours, notYoursBody, HttpStatusCode.Conflict, "model_conflict", "not_loaded_by_device");
        Assert.Equal(EngineState.Ready, _host.Chat.State);
    }

    [Fact]
    public async Task ALoadThatFailsForMemoryRestoresThePcsChoiceAndKeepsNoClaim()
    {
        var client = await PairedWithGrantAsync();
        _host.Chat.Options.FailLoadWithCode = ErrorCodes.InsufficientMemory;

        var (status, answer) = await CallAsync(client, HttpMethod.Post, Routes.DeviceModelLoad, Load("alpha"));
        AssertError(status, answer, HttpStatusCode.ServiceUnavailable, "insufficient_memory");
        Assert.Equal("pc-model", _host.Runtime.GetActive(ModelKinds.Chat)!.ModelId);
        Assert.Empty(_remote.Snapshot().LoadedModels);
    }

    [Fact]
    public async Task TakeBackUnloadsTheDevicesModelAndTheDeviceIsTold()
    {
        var client = await PairedWithGrantAsync();
        await CallAsync(client, HttpMethod.Post, Routes.DeviceModelLoad, Load("alpha", "cpu"));
        var loaded = Assert.Single(_remote.Snapshot().LoadedModels);
        Assert.Equal("cpu", loaded.Placement);

        Assert.True(await _remote.ModelControl.TakeBackAsync(ModelKinds.Chat, default));
        Assert.False(await _remote.ModelControl.TakeBackAsync(ModelKinds.Chat, default)); // nothing left to take back
        Assert.Equal(EngineState.Stopped, _host.Chat.State);
        Assert.Equal("pc-model", _host.Runtime.GetActive(ModelKinds.Chat)!.ModelId);
        Assert.Empty(_remote.Snapshot().LoadedModels);

        var (status, answer) = await CallAsync(client, HttpMethod.Post, Routes.DeviceModelUnload, Unload("alpha"));
        AssertError(status, answer, HttpStatusCode.Conflict, "model_conflict", "owner_changed");
    }

    [Fact]
    public async Task PlacementIsAskedPerLoadAndChangedForTheDevicesOwnModelOnly()
    {
        var client = await PairedWithGrantAsync();
        var other = await PairedWithGrantAsync("Other");
        await CallAsync(client, HttpMethod.Post, Routes.DeviceModelLoad, Load("alpha", "gpu"));

        var (status, list) = await CallAsync(client, HttpMethod.Get, Routes.DevicePlacement);
        Assert.Equal(HttpStatusCode.OK, status);
        var one = Assert.Single(list.GetProperty("placements").EnumerateArray().ToList());
        Assert.Equal("gpu", one.GetProperty("compute").GetString());

        var (setStatus, _) = await CallAsync(client, HttpMethod.Post, Routes.DevicePlacement,
            """{ "model_id": "alpha", "compute": "cpu", "cpu_threads": 2 }""");
        Assert.Equal(HttpStatusCode.OK, setStatus);
        Assert.Equal(EngineState.Ready, _host.Chat.State); // loaded again with the new placement
        Assert.Equal(2, _host.Runtime.GetActive(ModelKinds.Chat)!.Override!.CpuThreads);
        Assert.Equal("cpu", Assert.Single(_remote.Snapshot().LoadedModels).Placement);

        var (foreign, foreignBody) = await CallAsync(other, HttpMethod.Post, Routes.DevicePlacement,
            """{ "model_id": "alpha", "compute": "gpu" }""");
        AssertError(foreign, foreignBody, HttpStatusCode.Conflict, "model_conflict", "not_loaded_by_device");

        var (bad, badBody) = await CallAsync(client, HttpMethod.Post, Routes.DeviceModelLoad, Load("alpha", "tpu"));
        AssertError(bad, badBody, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task UnknownModelsAreNotFoundAndTheGrantSurvivesARestartButNotARemoval()
    {
        var client = await PairedWithGrantAsync();
        var (status, answer) = await CallAsync(client, HttpMethod.Post, Routes.DeviceModelLoad, Load("missing"));
        AssertError(status, answer, HttpStatusCode.NotFound, "not_found");

        Assert.True(new DeviceStore(_host.RootPath).Find(client.DeviceId!)!.ModelControl); // saved atomically in devices.json
        Assert.True(_remote.RemoveDevice(client.DeviceId!));
        var again = await PairAsync("Laptop");
        Assert.NotEqual(client.DeviceId, again.DeviceId);
        Assert.False(_remote.Devices.Find(again.DeviceId!)!.ModelControl); // pairing again never brings the grant back
    }
}
