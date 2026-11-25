using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Remote;
using Portal.AiHost.Core.Runtime;
using Portal.AiHost.Core.Tests.Remote;
using Portal.AiHost.FakeEngine;
using Portal.AiHost.Server;

namespace Portal.AiHost.Server.Tests;

/// <summary>The Devices view on the PC side: refusals on the wire, allowed models, stats and Forget.</summary>
public sealed class DevicesViewTests : IAsyncLifetime
{
    private sealed class Busy : IBusyDetector
    {
        public volatile bool IsBusyNow;
        public bool IsBusy() => IsBusyNow;
    }

    private TestHost _host = null!;
    private RemoteAccess _remote = null!;
    private HttpClient _http = null!;
    private FakeModelHost _models = null!;
    private readonly Busy _busy = new();
    private readonly List<DeviceAlert> _alerts = [];

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
            BusyDetector = _busy,
        });
        _remote.Alert += _alerts.Add;
        await _remote.ApplyAsync(new RemoteSettings { Enabled = true, Relay = false, AllowReplaceModels = true });
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
        return client;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> CallAsync(PairingClient client, HttpMethod method, string target,
        string? json = null, string? app = null)
    {
        var body = json is null ? [] : Encoding.UTF8.GetBytes(json);
        using var message = new HttpRequestMessage(method, target);
        foreach (var (name, value) in client.SignedHeaders(method.Method, target, DateTimeOffset.UtcNow, body))
        {
            message.Headers.TryAddWithoutValidation(name, value);
        }

        if (app is not null)
        {
            message.Headers.TryAddWithoutValidation("X-Portal-Client-App", app);
        }

        if (json is not null)
        {
            message.Content = Json(json);
        }

        using var response = await _http.SendAsync(message);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private const string Chat = """{ "messages": [ { "role": "user", "content": "hi" } ] }""";

    private static string Code(JsonElement body) => body.GetProperty("error").GetProperty("code").GetString()!;

    [Fact]
    public async Task APausedDeviceGetsDevicePausedWithRetryAfter()
    {
        var client = await PairAsync();
        Assert.True(_remote.SetPaused(client.DeviceId!, null, true));
        var (status, body) = await CallAsync(client, HttpMethod.Post, Routes.ChatCompletions, Chat);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("device_paused", Code(body));
        Assert.True(body.GetProperty("error").GetProperty("retry_after_ms").GetInt32() > 0);
        Assert.True(_remote.SetPaused(client.DeviceId!, null, false));
        var (_, again) = await CallAsync(client, HttpMethod.Post, Routes.ChatCompletions, Chat);
        Assert.NotEqual("device_paused", again.ValueKind == JsonValueKind.Object ? Code(again) : "");
    }

    [Fact]
    public async Task ApplicationPauseUsesTheLabelOnlyForThatApp()
    {
        var client = await PairAsync();
        Assert.True(_remote.SetPaused(client.DeviceId!, "Notes", true));
        var (_, paused) = await CallAsync(client, HttpMethod.Post, Routes.ChatCompletions, Chat, app: "Notes");
        Assert.Equal("device_paused", Code(paused));
        var (_, other) = await CallAsync(client, HttpMethod.Post, Routes.ChatCompletions, Chat, app: "Mail");
        Assert.NotEqual("device_paused", other.ValueKind == JsonValueKind.Object && other.TryGetProperty("error", out _) ? Code(other) : "");
        var summary = _remote.Snapshot().Devices.Single();
        Assert.Contains("Notes", summary.PausedApps);
        Assert.Contains(summary.Apps, a => a.Name == "Notes" && a.Paused);
    }

    [Fact]
    public async Task GamingModeRefusesWithHostBusyGamingOnlyWhileTheDetectorSaysBusy()
    {
        var client = await PairAsync();
        await _remote.ApplyAsync(new RemoteSettings { Enabled = true, Relay = false, GamingMode = true });
        _busy.IsBusyNow = true;
        var (status, body) = await CallAsync(client, HttpMethod.Post, Routes.ChatCompletions, Chat);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("host_busy_gaming", Code(body));
        Assert.True(_remote.Snapshot().GamingBusy);
        _busy.IsBusyNow = false;
        var (_, free) = await CallAsync(client, HttpMethod.Post, Routes.ChatCompletions, Chat);
        Assert.NotEqual("host_busy_gaming", free.ValueKind == JsonValueKind.Object && free.TryGetProperty("error", out _) ? Code(free) : "");
    }

    [Fact]
    public async Task TheDailyRequestLimitAnswers429DeviceLimitReached()
    {
        var client = await PairAsync();
        Assert.True(_remote.SetDeviceSettings(new SetDeviceSettingsCommand { DeviceId = client.DeviceId!, MaxRequestsPerDay = 1 }));
        await CallAsync(client, HttpMethod.Post, Routes.ChatCompletions, Chat); // the one allowed call
        var (status, body) = await CallAsync(client, HttpMethod.Post, Routes.ChatCompletions, Chat);
        Assert.Equal(HttpStatusCode.TooManyRequests, status);
        Assert.Equal("device_limit_reached", Code(body));
        Assert.True(body.GetProperty("error").GetProperty("retry_after_ms").GetInt32() > 0);
    }

    [Fact]
    public async Task QuietHoursAnswer503DeviceQuietHours()
    {
        var client = await PairAsync();
        var now = DateTime.Now;
        var start = now.AddMinutes(-5).ToString("HH:mm");
        var end = now.AddMinutes(30).ToString("HH:mm");
        if (string.CompareOrdinal(start, end) > 0 && now.Hour is 0 && now.Minute < 5)
        {
            return; // the window crosses midnight in the first minutes of the day; covered by the gate tests
        }

        Assert.True(_remote.SetDeviceSettings(new SetDeviceSettingsCommand { DeviceId = client.DeviceId!, QuietStart = start, QuietEnd = end }));
        var (status, body) = await CallAsync(client, HttpMethod.Post, Routes.ChatCompletions, Chat);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("device_quiet_hours", Code(body));
    }

    [Fact]
    public async Task AModelOutsideTheAllowedListGetsModelNotFound()
    {
        var client = await PairAsync();
        Assert.True(_remote.SetModelControl(client.DeviceId!, true));
        Assert.True(_remote.SetDeviceSettings(new SetDeviceSettingsCommand { DeviceId = client.DeviceId!, AllowedModels = ["alpha"] }));

        var (_, list) = await CallAsync(client, HttpMethod.Get, Routes.DeviceModels);
        Assert.Equal(["alpha"], list.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("id").GetString()));

        var (loadStatus, load) = await CallAsync(client, HttpMethod.Post, Routes.DeviceModelLoad, """{ "model_id": "beta" }""");
        Assert.Equal(HttpStatusCode.NotFound, loadStatus);
        Assert.Equal("model_not_found", Code(load));
        Assert.Equal("pc-model", _host.Runtime.GetActive(ModelKinds.Chat)!.ModelId);

        // The PC's current model is not on the list either, so a chat call is refused the same way.
        var (chatStatus, chat) = await CallAsync(client, HttpMethod.Post, Routes.ChatCompletions, Chat);
        Assert.Equal(HttpStatusCode.NotFound, chatStatus);
        Assert.Equal("model_not_found", Code(chat));

        var (okStatus, _) = await CallAsync(client, HttpMethod.Post, Routes.DeviceModelLoad, """{ "model_id": "alpha" }""");
        Assert.Equal(HttpStatusCode.OK, okStatus);
    }

    [Fact]
    public async Task StatsAreKeptPerAppAndForgetRemovesOnlyThatDevicesFile()
    {
        var a = await PairAsync("Laptop A");
        var b = await PairAsync("Laptop B");
        Assert.Equal(["paired", "paired"], _alerts.Select(x => x.Kind));
        foreach (var c in new[] { a, b })
        {
            await CallAsync(c, HttpMethod.Post, Routes.ChatCompletions, Chat, app: "Notes");
        }

        var summary = _remote.Snapshot().Devices.Single(d => d.DeviceId == a.DeviceId);
        var apps = Assert.Single(summary.Apps);
        Assert.Equal(("Notes", 1, 7), (apps.Name, apps.RequestsToday, apps.Last7Days.Count));
        var fileA = _remote.Stats.PathOf(a.DeviceId!)!;
        var fileB = _remote.Stats.PathOf(b.DeviceId!)!;
        Assert.True(File.Exists(fileA));
        Assert.True(File.Exists(fileB));

        var models = _models.Tree();
        Assert.True(_remote.RemoveDevice(a.DeviceId!));
        Assert.False(File.Exists(fileA));
        Assert.True(File.Exists(fileB));
        Assert.Equal(models, _models.Tree()); // no model file touched
        Assert.Single(_remote.Snapshot().Devices);
    }

    [Fact]
    public async Task RenameChangesTheNameShownAndKeepsTheDevice()
    {
        var client = await PairAsync("Old name");
        Assert.True(_remote.RenameDevice(client.DeviceId!, "Kitchen laptop"));
        Assert.Equal("Kitchen laptop", _remote.Snapshot().Devices.Single().Name);
        Assert.False(_remote.RenameDevice(client.DeviceId!, " "));
        Assert.False(_remote.RenameDevice("dev_unknown", "x"));
        var (status, _) = await CallAsync(client, HttpMethod.Get, Routes.DeviceModels);
        Assert.NotEqual(HttpStatusCode.Unauthorized, status); // still the same paired device
    }
}
