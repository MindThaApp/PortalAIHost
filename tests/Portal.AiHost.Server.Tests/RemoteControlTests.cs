using System.Net;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Tests.Remote;
using Portal.AiHost.Platform.Windows.Pipes;
using PortalAIHost.Service;

namespace Portal.AiHost.Server.Tests;

/// <summary>The "Other devices" messages of the admin pipe, through the real host shell.</summary>
public sealed class RemoteControlTests
{
    private static readonly RemoteAccessTestOptions Loopback = new()
    {
        BindAddress = IPAddress.Loopback,
        AllowSource = _ => true,
        Port = 0,
        Addresses = () => ["192.168.1.20"],
    };

    [Fact]
    public async Task RemoteAccessIsOffByDefaultAndNothingListens()
    {
        using var root = new TestRoot();
        await using var harness = await ShellHarness.StartAsync(root, Loopback);
        await using var client = await ControlPipeClient.ConnectAsync(harness.Paths.PipeName);

        Assert.False((await client.GetSettingsAsync()).Remote.Enabled);
        var status = await client.GetRemoteAsync();
        Assert.False(status.Enabled);
        Assert.False(status.Listening);
        Assert.Null(status.Problem);
        Assert.Empty(status.Devices);

        var refused = await Assert.ThrowsAsync<ControlPipeException>(() => client.StartPairingAsync());
        Assert.Equal(ErrorCodes.FeatureDisabled, refused.Code);
    }

    [Fact]
    public async Task TurningItOnOpensTheListenerAndAPairingCode()
    {
        using var root = new TestRoot();
        await using var harness = await ShellHarness.StartAsync(root, Loopback);
        await using var client = await ControlPipeClient.ConnectAsync(harness.Paths.PipeName);
        var changed = 0;
        client.RemoteChanged += (_, _) => Interlocked.Increment(ref changed);

        var settings = await client.GetSettingsAsync();
        settings.Remote.Enabled = true;
        await client.SetSettingsAsync(settings);

        var status = await client.GetRemoteAsync();
        Assert.True(status.Listening);
        Assert.True(status.Port > 0);
        Assert.NotNull(status.Fingerprint);

        var offer = await client.StartPairingAsync();
        Assert.Matches(@"^\d{6}$", offer.Code);
        Assert.StartsWith("portalai://pair?h=192.168.1.20&p=", offer.QrText);
        Assert.Contains("&c=" + offer.Code, offer.QrText);
        Assert.Equal(offer.Code, (await client.GetRemoteAsync()).Pairing!.Code);

        await client.CancelPairingAsync();
        Assert.Null((await client.GetRemoteAsync()).Pairing);
        await WaitAsync(() => Volatile.Read(ref changed) > 0);

        // Turning it off again closes the listener and keeps the saved settings in step.
        settings.Remote.Enabled = false;
        await client.SetSettingsAsync(settings);
        Assert.False((await client.GetRemoteAsync()).Listening);
        Assert.False(harness.Shell.Settings.Current.Remote.Enabled);
    }

    [Fact]
    public async Task APairedDeviceIsListedRemovedAndItsRequestsShowTheDeviceName()
    {
        using var root = new TestRoot();
        await using var harness = await ShellHarness.StartAsync(root, Loopback);
        await using var client = await ControlPipeClient.ConnectAsync(harness.Paths.PipeName);
        var settings = await client.GetSettingsAsync();
        settings.Remote.Enabled = true;
        await client.SetSettingsAsync(settings);

        var offer = await client.StartPairingAsync();
        using var device = new PairingClient("Kitchen tablet");
        var pairing = harness.Shell.Remote;
        var start = pairing.Devices; // the store the listener uses
        Assert.Empty(start.List());
        var id = await PairOverHttpAsync(harness, device, offer);

        var status = await client.GetRemoteAsync();
        var listed = Assert.Single(status.Devices);
        Assert.Equal(id, listed.DeviceId);
        Assert.Equal("Kitchen tablet", listed.Name);

        var pending = new TaskCompletionSource<PendingApproval>();
        client.ApprovalPending += (_, a) => pending.TrySetResult(a);
        await RequestAccessAsync(harness, device, offer);
        var approval = await pending.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Kitchen tablet", approval.DeviceName);
        Assert.StartsWith("unverified: ", approval.CallerIdentity);
        Assert.False(approval.CallerVerified);
        Assert.False(approval.CallerPortalApp);
        await client.ApproveAsync(approval.RequestId);
        await CollectTokenAsync(harness, device, offer, approval.RequestId);

        var clients = await client.ListClientsAsync();
        var summary = Assert.Single(clients.Clients);
        Assert.Equal(id, summary.DeviceId);
        Assert.Equal("Kitchen tablet", summary.DeviceName);

        await client.RemoveDeviceAsync(id);
        Assert.Empty((await client.GetRemoteAsync()).Devices);
        Assert.True(Assert.Single((await client.ListClientsAsync()).Clients).Revoked);
        var unknown = await Assert.ThrowsAsync<ControlPipeException>(() => client.RemoveDeviceAsync(id));
        Assert.Equal(ErrorCodes.NotFound, unknown.Code);
    }

    private static async Task<string> PairOverHttpAsync(ShellHarness harness, PairingClient device, PairingOfferInfo offer)
    {
        using var http = Pinned(harness, offer);
        using var start = await http.PostAsync(RemoteApi.PairStartPath, TestHost.Json(
            System.Text.Json.JsonSerializer.Serialize(device.Start(offer.Code), AiHostJsonContext.Default.PairStartRequest)));
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var startBody = System.Text.Json.JsonSerializer.Deserialize(await start.Content.ReadAsStringAsync(), AiHostJsonContext.Default.PairStartResponse)!;
        Assert.True(device.VerifyHost(startBody, offer.Code));
        using var finish = await http.PostAsync(RemoteApi.PairFinishPath, TestHost.Json(
            System.Text.Json.JsonSerializer.Serialize(device.Finish(offer.Code, startBody), AiHostJsonContext.Default.PairFinishRequest)));
        Assert.Equal(HttpStatusCode.OK, finish.StatusCode);
        var done = System.Text.Json.JsonSerializer.Deserialize(await finish.Content.ReadAsStringAsync(), AiHostJsonContext.Default.PairFinishResponse)!;
        Assert.True(device.VerifyDone(done, offer.Code, startBody));
        return done.DeviceId;
    }

    private static async Task RequestAccessAsync(ShellHarness harness, PairingClient device, PairingOfferInfo offer)
    {
        using var http = Pinned(harness, offer);
        var json = """{ "app_id": "com.mindthaapp.aiportal", "app_name": "AI Portal" }""";
        using var request = new HttpRequestMessage(HttpMethod.Post, Routes.AuthRequests) { Content = TestHost.Json(json) };
        foreach (var (name, value) in device.SignedHeaders("POST", Routes.AuthRequests, DateTimeOffset.UtcNow, System.Text.Encoding.UTF8.GetBytes(json)))
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    private static async Task CollectTokenAsync(ShellHarness harness, PairingClient device, PairingOfferInfo offer, string requestId)
    {
        using var http = Pinned(harness, offer);
        var target = $"{Routes.AuthRequests}/{requestId}?wait_ms=1000";
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        foreach (var (name, value) in device.SignedHeaders("GET", target, DateTimeOffset.UtcNow, []))
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await http.SendAsync(request);
        Assert.Contains("token", await response.Content.ReadAsStringAsync());
    }

    private static HttpClient Pinned(ShellHarness harness, PairingOfferInfo offer) => new(new SocketsHttpHandler
    {
        SslOptions = new System.Net.Security.SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, cert, _, _) => cert is not null &&
                Portal.AiHost.Core.Remote.RemoteCodec.Encode(System.Security.Cryptography.SHA256.HashData(cert.GetRawCertData())) == offer.Fingerprint,
        },
    })
    { BaseAddress = new Uri($"https://127.0.0.1:{offer.Port}") };

    private static async Task WaitAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < until)
        {
            await Task.Delay(20);
        }

        Assert.True(condition());
    }
}
