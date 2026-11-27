using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Generation;
using Portal.AiHost.Core.Remote;
using Portal.AiHost.Core.Tests.Remote;
using Portal.AiHost.FakeEngine;

namespace Portal.AiHost.Server.Tests;

/// <summary>Installed generation models backed by small files on disk; the claimed sizes drive the memory plan.</summary>
internal sealed class FakeGenerationModels : IGenerationModelSource
{
    private readonly string _folder;
    public long ImageBytes = 2L << 30;
    public long VideoDiffusionBytes = 1400L << 20;
    public bool Empty;
    public int InUse;
    public int Released;

    public FakeGenerationModels(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(folder);
        foreach (var name in new[] { "sd15.safetensors", "wan-diffusion.gguf", "umt5-xxl.gguf", "wan-vae.safetensors" })
        {
            File.WriteAllText(Path.Combine(folder, name), "model bytes of " + name);
        }
    }

    public Task<IReadOnlyList<GenerationModel>> ListAsync(string kind, CancellationToken ct)
    {
        if (Empty)
        {
            return Task.FromResult<IReadOnlyList<GenerationModel>>([]);
        }

        IReadOnlyList<GenerationModel> list = kind == ModelKinds.Image
            ? [new("sd15", "SD 1.5", kind, "sd1.5", [new(GenerationFileRoles.Model, Path.Combine(_folder, "sd15.safetensors"), ImageBytes)])]
            : kind == ModelKinds.Video
                ? [new("wan-1.3b", "Wan 1.3B", kind, "wan",
                [
                    new(GenerationFileRoles.Diffusion, Path.Combine(_folder, "wan-diffusion.gguf"), VideoDiffusionBytes),
                    new(GenerationFileRoles.T5xxl, Path.Combine(_folder, "umt5-xxl.gguf"), 6L << 30),
                    new(GenerationFileRoles.Vae, Path.Combine(_folder, "wan-vae.safetensors"), 250L << 20),
                ])]
                : [];
        return Task.FromResult(list);
    }

    public Task<IAsyncDisposable> UseAsync(string modelId, CancellationToken ct)
    {
        Interlocked.Increment(ref InUse);
        return Task.FromResult<IAsyncDisposable>(new Use(this));
    }

    private sealed class Use(FakeGenerationModels owner) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref owner.Released);
            return ValueTask.CompletedTask;
        }
    }

    public string Tree() => string.Join('|', Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories).Order()
        .Select(f => $"{f}:{new FileInfo(f).Length}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))}"));
}

/// <summary>An RX 6600-class PC: 8 GB graphics card (7.5 GB free), 64 GB RAM (50 GB free).</summary>
internal sealed class GenerationProbe : IHardwareProbe
{
    private const long GiB = 1L << 30;

    public Task<HardwareProfile> ProbeAsync(CancellationToken ct) => Task.FromResult(new HardwareProfile("windows", "x64", 12,
        64 * GiB, [new GpuProfile("AMD Radeon RX 6600", "amd", "0x1", "31.0", 8 * GiB, 0)], [], "fp"));

    public MemorySnapshot GetMemory() => new(64 * GiB, 50 * GiB, [(long)(7.5 * GiB)]);

    public bool IsOnBattery => false;
}

/// <summary>Image and video jobs over the loopback API with a fake engine (docs/api-v1.md, "Image and video jobs").</summary>
public sealed class GenerationJobTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private FakeGenerationEngine _engine = null!;
    private FakeGenerationModels _models = null!;
    private GenerationOutputFolder _output = null!;
    private GenerationService _service = null!;

    public async ValueTask InitializeAsync() => await StartAsync(hardware: null);

    private async Task StartAsync(IHardwareProbe? hardware)
    {
        _host = await TestHost.StartAsync(withModels: false, hardware: hardware);
        _engine = new FakeGenerationEngine();
        _models = new FakeGenerationModels(Path.Combine(_host.RootPath, "models"));
        _output = new GenerationOutputFolder(Path.Combine(_host.RootPath, "generated"));
        _service = new GenerationService(new GenerationServiceOptions
        {
            Runtime = _host.Runtime,
            Scheduler = _host.Scheduler,
            Engine = _engine,
            Models = _models,
            Output = _output,
            CancelWait = TimeSpan.FromSeconds(5),
        });
        _service.MapRoutes(_host.Runtime.Pipeline);
    }

    public async ValueTask DisposeAsync()
    {
        _engine.Gate?.TrySetResult();
        await _service.DisposeAsync();
        await _host.DisposeAsync();
    }

    private const string ImageBody = """{ "prompt": "a lighthouse at dusk", "width": 512, "height": 512, "steps": 4, "seed": 42 }""";

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpMethod method, string path, string token, string? json = null)
    {
        using var response = await _host.Http.SendAsync(_host.Authed(method, path, token, json));
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private async Task<string> CreateAsync(string token, string kind = ModelKinds.Image, string body = ImageBody)
    {
        var (status, job) = await SendAsync(HttpMethod.Post, Routes.JobsFor(kind)!, token, body);
        Assert.True(status == HttpStatusCode.Accepted, job.ToString());
        return job.GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> WaitAsync(string token, string id, string state, string kind = ModelKinds.Image)
    {
        JsonElement job = default;
        for (var i = 0; i < 200; i++)
        {
            (_, job) = await SendAsync(HttpMethod.Get, $"{Routes.JobsFor(kind)}/{id}", token);
            if (job.GetProperty("state").GetString() == state)
            {
                return job;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"Job {id} never reached {state}: {job}");
        return job;
    }

    private static void AssertError(HttpStatusCode status, JsonElement body, HttpStatusCode expected, string code)
    {
        Assert.Equal(expected, status);
        Assert.Equal(code, body.GetProperty("error").GetProperty("code").GetString());
    }

    private string[] WorkFolders() => Directory.Exists(_output.WorkRoot) ? Directory.GetDirectories(_output.WorkRoot) : [];

    [Fact]
    public async Task AnImageJobRunsToAUniqueResultFileAndServesIt()
    {
        var before = _models.Tree();
        var token = await _host.GetTokenAsync();
        var id = await CreateAsync(token);
        var job = await WaitAsync(token, id, GenerationJobStates.Succeeded);

        Assert.Equal(1.0, job.GetProperty("progress").GetDouble());
        var result = job.GetProperty("result");
        Assert.Equal(42, result.GetProperty("seed").GetInt64());
        Assert.Equal("image/png", result.GetProperty("content_type").GetString());
        var file = Path.Combine(_output.FinishedFolder(ModelKinds.Image), result.GetProperty("file_name").GetString()!);
        Assert.True(File.Exists(file));

        using var download = await _host.Http.SendAsync(_host.Authed(HttpMethod.Get, result.GetProperty("url").GetString()!, token));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("image/png", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal(await File.ReadAllBytesAsync(file), await download.Content.ReadAsByteArrayAsync());

        // The work folder (partial output, preview) is gone, the model files are untouched, the in-use mark was released.
        Assert.Empty(WorkFolders());
        Assert.Equal(before, _models.Tree());
        Assert.Equal(1, _models.InUse);
        Assert.Equal(1, _models.Released);

        var launch = Assert.Single(_engine.Launches);
        Assert.Equal(GenerationFamilies.Sd15, launch.Family);
        Assert.Equal(42, launch.Seed);
        Assert.StartsWith(_output.WorkRoot, launch.OutputPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TwoJobsNeverShareOrOverwriteAFile()
    {
        var token = await _host.GetTokenAsync();
        var folder = _output.FinishedFolder(ModelKinds.Image);
        Directory.CreateDirectory(folder);
        var mine = Path.Combine(folder, "keep-me.png");
        await File.WriteAllTextAsync(mine, "the user's own picture");

        var a = await CreateAsync(token);
        var b = await CreateAsync(token);
        var ja = await WaitAsync(token, a, GenerationJobStates.Succeeded);
        var jb = await WaitAsync(token, b, GenerationJobStates.Succeeded);
        var fa = ja.GetProperty("result").GetProperty("file_name").GetString();
        var fb = jb.GetProperty("result").GetProperty("file_name").GetString();
        Assert.NotEqual(fa, fb);
        Assert.Equal(3, Directory.GetFiles(folder).Length);
        Assert.Equal("the user's own picture", await File.ReadAllTextAsync(mine));
    }

    [Fact]
    public async Task JobsRunOneAtATimeThroughTheQueue()
    {
        var token = await _host.GetTokenAsync();
        _engine.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.Started += _ => started.TrySetResult();

        var first = await CreateAsync(token);
        var second = await CreateAsync(token, ModelKinds.Video, """{ "prompt": "waves", "frames": 9 }""");
        var third = await CreateAsync(token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var running = await WaitAsync(token, first, GenerationJobStates.Running);
        Assert.Equal("vulkan", running.GetProperty("plan").GetProperty("backend").GetString());
        var (_, queued) = await SendAsync(HttpMethod.Get, $"{Routes.VideoJobs}/{second}", token);
        Assert.Equal(GenerationJobStates.Queued, queued.GetProperty("state").GetString());
        Assert.True(queued.GetProperty("queue_position").GetInt32() >= 1);
        Assert.Equal(1, _engine.Running);

        _engine.Gate.TrySetResult();
        await WaitAsync(token, first, GenerationJobStates.Succeeded);
        await WaitAsync(token, second, GenerationJobStates.Succeeded, ModelKinds.Video);
        await WaitAsync(token, third, GenerationJobStates.Succeeded);
        Assert.Equal(1, _engine.MaxConcurrent);
        Assert.Equal(3, _engine.Launches.Count);
    }

    [Fact]
    public async Task CancelEndsARunningJobAndRemovesOnlyItsPartialFile()
    {
        var before = _models.Tree();
        var token = await _host.GetTokenAsync();
        var finished = _output.FinishedFolder(ModelKinds.Image);
        Directory.CreateDirectory(finished);
        await File.WriteAllTextAsync(Path.Combine(finished, "older.png"), "older result");

        _engine.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var partialSeen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.Started += l => partialSeen.TrySetResult(l.OutputPath);
        var id = await CreateAsync(token);
        var partial = await partialSeen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(File.Exists(partial));

        var (status, job) = await SendAsync(HttpMethod.Delete, $"{Routes.ImageJobs}/{id}", token);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(GenerationJobStates.Cancelled, job.GetProperty("state").GetString());
        Assert.False(File.Exists(partial));
        Assert.Empty(WorkFolders());
        Assert.Equal("older result", await File.ReadAllTextAsync(Path.Combine(finished, "older.png")));
        Assert.Single(Directory.GetFiles(finished));
        Assert.Equal(before, _models.Tree());

        // No result for a cancelled job; a second cancel just returns it.
        var (resultStatus, resultBody) = await SendAsync(HttpMethod.Get, $"{Routes.ImageJobs}/{id}/result", token);
        AssertError(resultStatus, resultBody, HttpStatusCode.NotFound, ErrorCodes.NotFound);
        (status, job) = await SendAsync(HttpMethod.Delete, $"{Routes.ImageJobs}/{id}", token);
        Assert.Equal(GenerationJobStates.Cancelled, job.GetProperty("state").GetString());
    }

    [Fact]
    public async Task CancelWhileQueuedNeverStartsTheEngine()
    {
        var token = await _host.GetTokenAsync();
        _engine.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.Started += _ => started.TrySetResult();
        var first = await CreateAsync(token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = await CreateAsync(token);

        var (_, job) = await SendAsync(HttpMethod.Delete, $"{Routes.ImageJobs}/{second}", token);
        Assert.Equal(GenerationJobStates.Cancelled, job.GetProperty("state").GetString());
        _engine.Gate.TrySetResult();
        await WaitAsync(token, first, GenerationJobStates.Succeeded);
        Assert.Single(_engine.Launches);
    }

    [Fact]
    public async Task AFailedRunRemovesItsPartialFileAndReportsTheCode()
    {
        var token = await _host.GetTokenAsync();
        _engine.FailWith = ErrorCodes.InsufficientMemory;
        var id = await CreateAsync(token);
        var job = await WaitAsync(token, id, GenerationJobStates.Failed);
        Assert.Equal(ErrorCodes.InsufficientMemory, job.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(WorkFolders());
        Assert.False(Directory.Exists(_output.FinishedFolder(ModelKinds.Image)) &&
            Directory.GetFiles(_output.FinishedFolder(ModelKinds.Image)).Length > 0);
    }

    [Fact]
    public async Task ARunningJobReservesItsMemoryForOtherEngines()
    {
        var token = await _host.GetTokenAsync();
        _engine.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.Started += _ => started.TrySetResult();
        Assert.Equal(0, _host.Runtime.CommittedExcept(ModelKinds.Chat).RamBytes);
        var id = await CreateAsync(token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(_host.Runtime.CommittedExcept(ModelKinds.Chat).RamBytes > 0);

        _engine.Gate.TrySetResult();
        await WaitAsync(token, id, GenerationJobStates.Succeeded);
        for (var i = 0; i < 100 && _host.Runtime.GenerationReserved().RamBytes > 0; i++)
        {
            await Task.Delay(20);
        }

        Assert.Equal(0, _host.Runtime.CommittedExcept(ModelKinds.Chat).RamBytes);
    }

    [Fact]
    public async Task EngineMissingAndModelNotInstalledAreClearErrors()
    {
        var token = await _host.GetTokenAsync();
        _engine.Present = false;
        var (status, body) = await SendAsync(HttpMethod.Post, Routes.ImageJobs, token, ImageBody);
        AssertError(status, body, HttpStatusCode.ServiceUnavailable, ErrorCodes.EngineMissing);

        _engine.Present = true;
        (status, body) = await SendAsync(HttpMethod.Post, Routes.ImageJobs, token, """{ "prompt": "x", "model": "not-here" }""");
        AssertError(status, body, HttpStatusCode.NotFound, ErrorCodes.ModelNotInstalled);
        Assert.Equal("model", body.GetProperty("error").GetProperty("param").GetString());

        _models.Empty = true;
        (status, body) = await SendAsync(HttpMethod.Post, Routes.VideoJobs, token, """{ "prompt": "x" }""");
        AssertError(status, body, HttpStatusCode.NotFound, ErrorCodes.ModelNotInstalled);
        Assert.Equal("video", body.GetProperty("error").GetProperty("details").GetProperty("kind").GetString());
        Assert.Empty(_engine.Launches);
        Assert.Empty(WorkFolders());
    }

    [Fact]
    public async Task BadRequestsAreRefusedBeforeAnythingIsQueued()
    {
        var token = await _host.GetTokenAsync();
        foreach (var (kind, json, code, param) in new[]
        {
            (ModelKinds.Image, """{ "prompt": "" }""", ErrorCodes.InvalidRequest, "prompt"),
            (ModelKinds.Image, """{ "prompt": "x", "width": 500 }""", ErrorCodes.InvalidRequest, "width"),
            (ModelKinds.Image, """{ "prompt": "x", "steps": 0 }""", ErrorCodes.InvalidRequest, "steps"),
            (ModelKinds.Image, """{ "prompt": "x", "frames": 33 }""", ErrorCodes.UnsupportedParameter, "frames"),
            (ModelKinds.Image, """{ "prompt": "x", "init_image": "bm90IGEgcGljdHVyZQ==" }""", ErrorCodes.InvalidRequest, "init_image"),
            (ModelKinds.Video, """{ "prompt": "x", "init_image": "iVBORw0KGgoAAAANSUhEUg==" }""", ErrorCodes.UnsupportedParameter, "init_image"),
            (ModelKinds.Image, """{ "prompt": "x", "sampler": "rm -rf" }""", ErrorCodes.InvalidRequest, "sampler"),
        })
        {
            var (status, body) = await SendAsync(HttpMethod.Post, Routes.JobsFor(kind)!, token, json);
            Assert.Equal(code, body.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(param, body.GetProperty("error").GetProperty("param").GetString());
            Assert.True(status is HttpStatusCode.BadRequest);
        }

        Assert.Empty(_engine.Launches);
        Assert.Empty(WorkFolders());
    }

    [Fact]
    public async Task JobsNeedATokenAndBelongToTheirApp()
    {
        using (var anonymous = await _host.Http.PostAsync(Routes.ImageJobs, TestHost.Json(ImageBody)))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        var mine = await _host.GetTokenAsync();
        var other = await _host.GetTokenAsync("com.mindthaapp.other");
        var id = await CreateAsync(mine);
        await WaitAsync(mine, id, GenerationJobStates.Succeeded);
        foreach (var path in new[] { $"{Routes.ImageJobs}/{id}", $"{Routes.ImageJobs}/{id}/result", $"{Routes.VideoJobs}/{id}" })
        {
            var (status, body) = await SendAsync(HttpMethod.Get, path, path.StartsWith(Routes.VideoJobs, StringComparison.Ordinal) ? mine : other);
            AssertError(status, body, HttpStatusCode.NotFound, ErrorCodes.NotFound);
        }

        var (deleteStatus, deleteBody) = await SendAsync(HttpMethod.Delete, $"{Routes.ImageJobs}/{id}", other);
        AssertError(deleteStatus, deleteBody, HttpStatusCode.NotFound, ErrorCodes.NotFound);
        var (_, list) = await SendAsync(HttpMethod.Get, Routes.ImageJobs, other);
        Assert.Equal(0, list.GetProperty("data").GetArrayLength());
        (_, list) = await SendAsync(HttpMethod.Get, Routes.ImageJobs, mine);
        Assert.Equal(1, list.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public async Task AJobThatCannotFitIsRefusedAndAPictureFallsBackToTheProcessor()
    {
        await _service.DisposeAsync();
        await _host.DisposeAsync();
        await StartAsync(new GenerationProbe());
        var token = await _host.GetTokenAsync();

        // Video: 30 GB of diffusion weights do not fit the 8 GB card even with offload, and video never falls back to the processor.
        _models.VideoDiffusionBytes = 30L << 30;
        var (status, body) = await SendAsync(HttpMethod.Post, Routes.VideoJobs, token, """{ "prompt": "waves" }""");
        AssertError(status, body, HttpStatusCode.ServiceUnavailable, ErrorCodes.InsufficientMemory);

        // Picture: 60 GB fits nowhere.
        _models.ImageBytes = 60L << 30;
        (status, body) = await SendAsync(HttpMethod.Post, Routes.ImageJobs, token, ImageBody);
        AssertError(status, body, HttpStatusCode.ServiceUnavailable, ErrorCodes.InsufficientMemory);
        Assert.Empty(_engine.Launches);

        // Picture: 20 GB does not fit the card at once, so the weights stay in RAM and stream to it (offload).
        _models.ImageBytes = 20L << 30;
        var id = await CreateAsync(token);
        var job = await WaitAsync(token, id, GenerationJobStates.Succeeded);
        Assert.Equal("vulkan", job.GetProperty("plan").GetProperty("backend").GetString());
        Assert.Contains("weights", job.GetProperty("plan").GetProperty("on_cpu").EnumerateArray().Select(e => e.GetString()));
        Assert.True(_engine.Launches.Last().Placement.OffloadToCpu);

        // Picture: 32 GB does not fit the card even with offload but fits the RAM budget, so it runs on the processor.
        _models.ImageBytes = 32L << 30;
        id = await CreateAsync(token);
        job = await WaitAsync(token, id, GenerationJobStates.Succeeded);
        Assert.Equal("cpu", job.GetProperty("plan").GetProperty("backend").GetString());
        Assert.Equal("cpu", _engine.Launches.Last().Placement.Backend);

        // The small Wan model fits the card with the text encoder on the processor.
        _models.VideoDiffusionBytes = 1400L << 20;
        id = await CreateAsync(token, ModelKinds.Video, """{ "prompt": "waves", "width": 832, "height": 480, "frames": 33 }""");
        job = await WaitAsync(token, id, GenerationJobStates.Succeeded, ModelKinds.Video);
        var plan = job.GetProperty("plan");
        Assert.Equal("vulkan", plan.GetProperty("backend").GetString());
        Assert.Contains("text_encoder", plan.GetProperty("on_cpu").EnumerateArray().Select(e => e.GetString()));
        var launch = _engine.Launches.Last();
        Assert.True(launch.Placement.ClipOnCpu);
        Assert.True(launch.Placement.FlashAttention);
        Assert.Equal(33, launch.Frames);
    }
}

/// <summary>The job routes for paired devices over real TLS: device token, Model control grant and allowed models.</summary>
public sealed class GenerationDeviceTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private RemoteAccess _remote = null!;
    private HttpClient _http = null!;
    private FakeGenerationEngine _engine = null!;
    private GenerationService _service = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.StartAsync(withModels: false);
        _engine = new FakeGenerationEngine();
        _service = new GenerationService(new GenerationServiceOptions
        {
            Runtime = _host.Runtime,
            Scheduler = _host.Scheduler,
            Engine = _engine,
            Models = new FakeGenerationModels(Path.Combine(_host.RootPath, "models")),
            Output = new GenerationOutputFolder(Path.Combine(_host.RootPath, "generated")),
        });
        _service.MapRoutes(_host.Runtime.Pipeline);
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
        });
        _service.DeviceMayUse = (device, model) => _remote.Devices.Find(device) is not { } d || d.AllowedModels.Count == 0 ||
            d.AllowedModels.Contains(model, StringComparer.Ordinal);
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
        await _service.DisposeAsync();
        await _remote.DisposeAsync();
        await _host.DisposeAsync();
    }

    private static string Pad(string b64Url)
    {
        var s = b64Url.Replace('-', '+').Replace('_', '/');
        return s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private async Task<PairingClient> PairAsync()
    {
        var offer = _remote.StartPairing();
        var client = new PairingClient("Laptop");
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
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await _http.SendAsync(message);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private async Task<string> DeviceTokenAsync(PairingClient client)
    {
        var (status, created) = await CallAsync(client, HttpMethod.Post, Routes.AuthRequests,
            """{ "app_id": "com.mindthaapp.aiportal", "app_name": "AI Portal" }""");
        Assert.Equal(HttpStatusCode.Accepted, status);
        var id = created.GetProperty("request_id").GetString()!;
        await _host.Registry.ApproveAsync(id, default);
        var (_, poll) = await CallAsync(client, HttpMethod.Get, $"{Routes.AuthRequests}/{id}?wait_ms=1000");
        return poll.GetProperty("token").GetString()!;
    }

    private const string Body = """{ "prompt": "a red kite", "steps": 4 }""";

    [Fact]
    public async Task ADeviceNeedsItsTokenAndModelControl()
    {
        var client = await PairAsync();

        // Signed but without a token: the pipeline refuses before the job service runs.
        var (status, body) = await CallAsync(client, HttpMethod.Post, Routes.ImageJobs, Body);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(ErrorCodes.AuthRequired, body.GetProperty("error").GetProperty("code").GetString());

        var token = await DeviceTokenAsync(client);
        (status, body) = await CallAsync(client, HttpMethod.Post, Routes.ImageJobs, Body, token);
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal(ErrorCodes.ModelControlRequired, body.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(_engine.Launches);

        Assert.True(_remote.SetModelControl(client.DeviceId!, true));
        (status, body) = await CallAsync(client, HttpMethod.Post, Routes.ImageJobs, Body, token);
        Assert.Equal(HttpStatusCode.Accepted, status);
        var id = body.GetProperty("id").GetString()!;
        for (var i = 0; i < 200 && body.GetProperty("state").GetString() != GenerationJobStates.Succeeded; i++)
        {
            await Task.Delay(25);
            (_, body) = await CallAsync(client, HttpMethod.Get, $"{Routes.ImageJobs}/{id}", token: token);
        }

        Assert.Equal(GenerationJobStates.Succeeded, body.GetProperty("state").GetString());

        // The job belongs to the device's app: the same app id on this PC does not see it.
        var local = await _host.GetTokenAsync("com.mindthaapp.aiportal");
        using var fromPc = await _host.Http.SendAsync(_host.Authed(HttpMethod.Get, $"{Routes.ImageJobs}/{id}", local));
        Assert.Equal(HttpStatusCode.NotFound, fromPc.StatusCode);

        // Taking the grant back stops new jobs.
        Assert.True(_remote.SetModelControl(client.DeviceId!, false));
        (status, _) = await CallAsync(client, HttpMethod.Post, Routes.ImageJobs, Body, token);
        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    [Fact]
    public async Task ADeviceLimitedToOtherModelsIsRefused()
    {
        var client = await PairAsync();
        var token = await DeviceTokenAsync(client);
        Assert.True(_remote.SetModelControl(client.DeviceId!, true));
        Assert.True(_remote.SetDeviceSettings(new SetDeviceSettingsCommand { DeviceId = client.DeviceId!, AllowedModels = ["some-chat-model"] }));
        var (status, body) = await CallAsync(client, HttpMethod.Post, Routes.ImageJobs, Body, token);
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal(ErrorCodes.ModelNotFound, body.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(_engine.Launches);
    }

    [Fact]
    public async Task APausedDeviceCannotStartAJob()
    {
        var client = await PairAsync();
        var token = await DeviceTokenAsync(client);
        Assert.True(_remote.SetModelControl(client.DeviceId!, true));
        Assert.True(_remote.SetPaused(client.DeviceId!, null, true));
        var (status, body) = await CallAsync(client, HttpMethod.Post, Routes.ImageJobs, Body, token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal(ErrorCodes.DevicePaused, body.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(_engine.Launches);
    }
}
