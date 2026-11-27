using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Auth;
using Portal.AiHost.Core.Runtime;
using Portal.AiHost.Core.Scheduling;
using Portal.AiHost.FakeEngine;

namespace Portal.AiHost.Server.Tests;

/// <summary>A temp folder this test run created, marked so cleanup only ever removes its own folders.</summary>
public sealed class TestRoot : IDisposable
{
    public const string MarkerName = ".portal-ai-host-test-root";

    public TestRoot()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pah-server-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        File.WriteAllText(System.IO.Path.Combine(Path, MarkerName), "test");
    }

    public string Path { get; }

    public void Dispose()
    {
        if (File.Exists(System.IO.Path.Combine(Path, MarkerName)) &&
            Path.StartsWith(System.IO.Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

public sealed class TestKeyProtector : IHostKeyProtector
{
    public byte[] Protect(byte[] plaintext) => [0x50, .. plaintext.Select(b => (byte)(b ^ 0x33))];

    public byte[] Unprotect(byte[] protectedData) => protectedData[1..].Select(b => (byte)(b ^ 0x33)).ToArray();
}

/// <summary>Every connection belongs to this test process, as the same user.</summary>
public sealed class SameUserResolver : ICallerIdentityResolver
{
    public int Calls;

    public CallerIdentity? Resolve(IPEndPoint remote, IPEndPoint local)
    {
        Interlocked.Increment(ref Calls);
        return new CallerIdentity(Environment.ProcessId, null, @"C:\tests\client.exe", Verified: false, SameUser: true);
    }
}

/// <summary>In-process host: runtime + Kestrel on a test port + FakeEngine supervisors.</summary>
public sealed class TestHost : IAsyncDisposable
{
    private readonly TestRoot _root = new();

    private TestHost()
    {
    }

    public FakeEngineSupervisor Chat { get; private set; } = null!;
    public FakeEngineSupervisor Embed { get; private set; } = null!;

    /// <summary>The second chat engine slot (only when started with <c>secondSlot: true</c>).</summary>
    public FakeEngineSupervisor? Chat2 { get; private set; }
    public ClientRegistry Registry { get; private set; } = null!;
    public FairScheduler Scheduler { get; private set; } = null!;
    public AiHostRuntime Runtime { get; private set; } = null!;
    public Portal.AiHost.Core.Voice.VoiceService? Voice { get; private set; }
    public AiHostServer Server { get; private set; } = null!;
    public SameUserResolver Resolver { get; } = new();
    public HttpClient Http { get; private set; } = null!;
    public string RootPath => _root.Path;
    public string DiscoveryPath => DiscoveryFileWriter.PathFor(_root.Path);

    public static async Task<TestHost> StartAsync(FakeEngineOptions? chat = null, int maxRequestBytes = ApiLimits.MaxRequestBytes,
        bool withModels = true, int parallelSlots = 1, HostSettings? settings = null, IEaseOffSensors? sensors = null,
        bool secondSlot = false, FakeEngineOptions? chat2 = null, IHardwareProbe? hardware = null,
        Portal.AiHost.Core.Voice.VoiceService? voice = null)
    {
        var host = new TestHost();
        host.Voice = voice;
        host.Chat = new FakeEngineSupervisor(ModelKinds.Chat, chat);
        host.Embed = new FakeEngineSupervisor(ModelKinds.Embedding);
        if (secondSlot)
        {
            host.Chat2 = new FakeEngineSupervisor(ModelSlots.Second(ModelKinds.Chat), chat2);
        }

        host.Registry = new ClientRegistry(new ClientRegistryOptions { DataDirectory = host._root.Path }, new TestKeyProtector());
        host.Scheduler = new FairScheduler(new FairSchedulerOptions { DefaultSlots = parallelSlots });
        host.Runtime = await AiHostRuntime.StartAsync(new AiHostRuntimeOptions
        {
            Registry = host.Registry,
            Scheduler = host.Scheduler,
            Supervisors = new Dictionary<string, IEngineSupervisor>(
                host.Chat2 is null
                    ? [new(ModelKinds.Chat, host.Chat), new(ModelKinds.Embedding, host.Embed)]
                    : [new(ModelKinds.Chat, host.Chat), new(ModelKinds.Embedding, host.Embed), new(ModelSlots.Second(ModelKinds.Chat), host.Chat2)]),
            InstanceId = "6f1c2a9e4b7d4c0f9a51d3e8b2c47a10",
            ProgressInterval = TimeSpan.FromMilliseconds(200),
            Pipeline = new() { MaxRequestBytes = maxRequestBytes },
            Settings = settings is null ? null : () => settings,
            EaseOffSensors = sensors,
            Hardware = hardware,
            Voice = voice,
        });
        if (settings is not null)
        {
            await host.Runtime.ApplyPerformanceAsync(default);
        }

        if (withModels)
        {
            await host.Runtime.SetActiveAsync(ModelKinds.Chat, new ActiveModel(ModelKinds.Chat, "qwen3-30b-a3b-q4km",
                "Qwen3 30B A3B", [@"C:\models\fake.gguf"], ContextTokens: 32768, ParallelSlots: parallelSlots,
                SupportsTools: true, SupportsReasoning: true), default);
            await host.Runtime.SetActiveAsync(ModelKinds.Embedding, new ActiveModel(ModelKinds.Embedding, "embed-small-q8",
                "Small embeddings", [@"C:\models\embed.gguf"], EmbeddingDimensions: 8), default);
        }

        host.Server = await AiHostServer.StartAsync(host.Runtime, new AiHostServerOptions
        {
            TestMode = true,
            CallerResolver = host.Resolver,
            DiscoveryPath = host.DiscoveryPath,
            MaxRequestBytes = maxRequestBytes,
        });
        host.Http = new HttpClient(new SocketsHttpHandler { MaxResponseDrainSize = 0 }) { BaseAddress = new Uri(host.Server.BaseUrl), Timeout = TimeSpan.FromSeconds(30) };
        return host;
    }

    /// <summary>Runs the real access flow: request, approve in the host, collect the token.</summary>
    public async Task<string> GetTokenAsync(string appId = "com.mindthaapp.voiceportal")
    {
        using var create = await Http.PostAsync(Routes.AuthRequests, Json($$"""
            { "app_id": "{{appId}}", "app_name": "Voice Portal", "app_version": "1.3.0", "reason": "Meeting minutes" }
            """));
        Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
        var id = JsonDocument.Parse(await create.Content.ReadAsStringAsync()).RootElement.GetProperty("request_id").GetString()!;
        await Registry.ApproveAsync(id, default);
        using var poll = await Http.GetAsync($"{Routes.AuthRequests}/{id}?wait_ms=1000");
        var token = JsonDocument.Parse(await poll.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString();
        Assert.NotNull(token);
        return token!;
    }

    public static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    public HttpRequestMessage Authed(HttpMethod method, string path, string token, string? json = null)
    {
        var msg = new HttpRequestMessage(method, path);
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (json is not null)
        {
            msg.Content = Json(json);
        }

        return msg;
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await Server.DisposeAsync();
        await Runtime.DisposeAsync();
        if (Voice is not null)
        {
            await Voice.DisposeAsync();
        }

        Registry.Dispose();
        Scheduler.Dispose();
        _root.Dispose();
    }
}
