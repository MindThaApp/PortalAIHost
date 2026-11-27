using System.Net;
using System.Text.Json;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Generation;
using Portal.AiHost.FakeEngine;

namespace Portal.AiHost.Server.Tests;

/// <summary>A stand-in for the upscale helper: writes a new file at the requested path, or waits to be cancelled.</summary>
internal sealed class FakeUpscaleHelper : IUpscaleHelper
{
    public bool EnginePresent { get; set; } = true;
    public bool Hold { get; set; }
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<(string Input, string Output, int Scale, string Device)> Calls { get; } = [];

    public async Task UpscaleAsync(string modelPath, string input, string outputPath, int scale, string device,
        Action<double> progress, CancellationToken ct)
    {
        Calls.Add((input, outputPath, scale, device));
        await File.WriteAllBytesAsync(outputPath, [0x89, 0x50, 0x4E, 0x47, 9, 9, 9], ct);
        Started.TrySetResult();
        progress(0.5);
        if (Hold)
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
    }
}

/// <summary>Image upscale jobs and the estimate route (docs/api-v1.md).</summary>
public sealed class UpscaleJobTests : IAsyncLifetime
{
    private const string ImageBody = """{ "prompt": "a lighthouse at dusk", "width": 512, "height": 512, "steps": 4, "seed": 42 }""";
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 2, 0, 0, 0, 2, 0, 8, 2];

    private TestHost _host = null!;
    private FakeUpscaleHelper _helper = null!;
    private GenerationOutputFolder _output = null!;
    private GenerationService _service = null!;
    private UploadService _uploads = null!;
    private double _rate = 1;
    private bool _bestInstalled;

    private string ModelFolder => Path.Combine(_host.RootPath, "models", "upscale");

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.StartAsync(withModels: false);
        Directory.CreateDirectory(ModelFolder);
        await File.WriteAllBytesAsync(Path.Combine(ModelFolder, "x4.onnx"), [1, 2, 3]);
        _helper = new FakeUpscaleHelper();
        _output = new GenerationOutputFolder(Path.Combine(_host.RootPath, "generated"));
        _uploads = new UploadService(new UploadServiceOptions
        {
            Folder = Path.Combine(_host.RootPath, "uploads"),
            SweepInterval = TimeSpan.Zero,
        });
        _uploads.MapRoutes(_host.Runtime.Pipeline);
        _service = new GenerationService(new GenerationServiceOptions
        {
            Runtime = _host.Runtime,
            Scheduler = _host.Scheduler,
            Engine = new FakeGenerationEngine(),
            Models = new FakeGenerationModels(Path.Combine(_host.RootPath, "models")),
            Output = _output,
            Uploads = _uploads,
            Upscaler = _helper,
            UpscaleModelFolder = ModelFolder,
            UpscaleBestInstalled = () => _bestInstalled,
            UpscaleRate = (_, _) => _rate,
            CancelWait = TimeSpan.FromSeconds(5),
        });
        _service.MapRoutes(_host.Runtime.Pipeline);
    }

    public async ValueTask DisposeAsync()
    {
        await _service.DisposeAsync();
        _uploads.Dispose();
        await _host.DisposeAsync();
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpMethod method, string path, string token, string? json = null)
    {
        using var response = await _host.Http.SendAsync(_host.Authed(method, path, token, json));
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 || response.Content.Headers.ContentType?.MediaType != "application/json"
            ? default
            : JsonDocument.Parse(text).RootElement.Clone());
    }

    private async Task<JsonElement> WaitAsync(string token, string root, string id, string state)
    {
        JsonElement job = default;
        for (var i = 0; i < 300; i++)
        {
            (_, job) = await SendAsync(HttpMethod.Get, $"{root}/{id}", token);
            if (job.GetProperty("state").GetString() == state)
            {
                return job;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"Job {id} never reached {state}: {job}");
        return job;
    }

    private async Task<string> ImageJobAsync(string token)
    {
        var (status, job) = await SendAsync(HttpMethod.Post, Routes.ImageJobs, token, ImageBody);
        Assert.True(status == HttpStatusCode.Accepted, job.ToString());
        var id = job.GetProperty("id").GetString()!;
        await WaitAsync(token, Routes.ImageJobs, id, GenerationJobStates.Succeeded);
        return id;
    }

    private async Task<string> UploadAsync(string token)
    {
        var msg = _host.Authed(HttpMethod.Post, Routes.Uploads, token);
        msg.Content = new ByteArrayContent(Png);
        msg.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        using var response = await _host.Http.SendAsync(msg);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, text);
        return JsonDocument.Parse(text).RootElement.GetProperty("id").GetString()!;
    }

    private static string JobBody(string id, string extra = "") => $$"""{ "source": { "job": "{{id}}" }{{extra}} }""";

    private static void AssertError(HttpStatusCode status, JsonElement body, HttpStatusCode expected, string code)
    {
        Assert.Equal(expected, status);
        Assert.Equal(code, body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task AFinishedImageJobIsUpscaledToANewPublishedFileAndTheInputIsKept()
    {
        var token = await _host.GetTokenAsync();
        var id = await ImageJobAsync(token);
        var inputPath = Directory.GetFiles(_output.FinishedFolder(ModelKinds.Image)).Single();
        var before = await File.ReadAllBytesAsync(inputPath);

        var (status, job) = await SendAsync(HttpMethod.Post, Routes.ImageUpscales, token, JobBody(id, """, "scale": 4, "tier": "fast", "device": "cpu" """));
        Assert.True(status == HttpStatusCode.Accepted, job.ToString());
        Assert.Equal("generation.upscale", job.GetProperty("object").GetString());
        var upId = job.GetProperty("id").GetString()!;
        var done = await WaitAsync(token, Routes.ImageUpscales, upId, GenerationJobStates.Succeeded);

        var call = Assert.Single(_helper.Calls);
        Assert.Equal((4, "cpu"), (call.Scale, call.Device));
        var files = Directory.GetFiles(_output.FinishedFolder(ModelKinds.Image));
        Assert.Equal(2, files.Length);
        Assert.Equal(before, await File.ReadAllBytesAsync(inputPath));
        Assert.EndsWith(".png", done.GetProperty("result").GetProperty("file_name").GetString());

        using var download = await _host.Http.SendAsync(_host.Authed(HttpMethod.Get, done.GetProperty("result").GetProperty("url").GetString()!, token));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Empty(Directory.Exists(_output.WorkRoot) ? Directory.GetDirectories(_output.WorkRoot) : []);

        var (_, list) = await SendAsync(HttpMethod.Get, Routes.ImageUpscales, token);
        Assert.Single(list.GetProperty("data").EnumerateArray());
    }

    [Fact]
    public async Task AnUploadIsAcceptedAndAnotherAppsUploadOrJobIsRefused()
    {
        var owner = await _host.GetTokenAsync();
        var other = await _host.GetTokenAsync("com.example.other");
        var jobId = await ImageJobAsync(owner);
        var uploadId = await UploadAsync(owner);

        var (ok, _) = await SendAsync(HttpMethod.Post, Routes.ImageUpscales, owner, $$"""{ "source": { "upload": "{{uploadId}}" } }""");
        Assert.Equal(HttpStatusCode.Accepted, ok);

        var (s1, b1) = await SendAsync(HttpMethod.Post, Routes.ImageUpscales, other, JobBody(jobId));
        AssertError(s1, b1, HttpStatusCode.NotFound, ErrorCodes.NotFound);
        var (s2, b2) = await SendAsync(HttpMethod.Post, Routes.ImageUpscales, other, $$"""{ "source": { "upload": "{{uploadId}}" } }""");
        AssertError(s2, b2, HttpStatusCode.NotFound, ErrorCodes.NotFound);
        var (s3, b3) = await SendAsync(HttpMethod.Post, Routes.ImageUpscales + "/estimate", other, JobBody(jobId));
        AssertError(s3, b3, HttpStatusCode.NotFound, ErrorCodes.NotFound);

        // Another app cannot see or cancel the upscale either.
        var (_, list) = await SendAsync(HttpMethod.Get, Routes.ImageUpscales, other);
        Assert.Empty(list.GetProperty("data").EnumerateArray());
    }

    [Theory]
    [InlineData("""{ "source": { "job": "C:\\Users\\me\\a.png" } }""")]
    [InlineData("""{ "source": { "upload": "..\\..\\a.png" } }""")]
    [InlineData("""{ "source": { "upload": "/etc/passwd" } }""")]
    [InlineData("""{ "source": { "job": "\\\\server\\share\\a.png" } }""")]
    [InlineData("""{ "path": "C:\\a.png" }""")]
    [InlineData("""{ "source": { } }""")]
    public async Task APathIsRefused(string body)
    {
        var token = await _host.GetTokenAsync();
        var (status, error) = await SendAsync(HttpMethod.Post, Routes.ImageUpscales, token, body);
        AssertError(status, error, HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest);
        Assert.Empty(_helper.Calls);
    }

    [Theory]
    [InlineData(""", "tier": "best" """)]
    [InlineData(""", "tier": "ultra" """)]
    [InlineData(""", "scale": 3 """)]
    [InlineData(""", "device": "tpu" """)]
    public async Task ParametersAreValidated(string extra)
    {
        var token = await _host.GetTokenAsync();
        var id = await ImageJobAsync(token);
        var (status, error) = await SendAsync(HttpMethod.Post, Routes.ImageUpscales, token, JobBody(id, extra));
        AssertError(status, error, HttpStatusCode.BadRequest, ErrorCodes.UnsupportedParameter);
    }

    [Fact]
    public async Task BestIsAcceptedOnlyWhileTheAddOnIsInstalled()
    {
        var token = await _host.GetTokenAsync();
        var id = await ImageJobAsync(token);
        _bestInstalled = true;
        var (status, _) = await SendAsync(HttpMethod.Post, Routes.ImageUpscales, token, JobBody(id, """, "tier": "best" """));
        Assert.Equal(HttpStatusCode.Accepted, status);
    }

    [Fact]
    public async Task NoModelAndNoEngineAreReportedAndJobsReturnEngineMissing()
    {
        var token = await _host.GetTokenAsync();
        var id = await ImageJobAsync(token);
        Assert.True(_service.Availability().Upscale!.Image);

        _helper.EnginePresent = false;
        var engineOff = _service.Availability().Upscale!;
        Assert.False(engineOff.Image);
        Assert.Equal("no_engine", engineOff.Detail);
        var (s1, b1) = await SendAsync(HttpMethod.Post, Routes.ImageUpscales, token, JobBody(id));
        AssertError(s1, b1, HttpStatusCode.ServiceUnavailable, ErrorCodes.EngineMissing);

        _helper.EnginePresent = true;
        File.Delete(Path.Combine(ModelFolder, "x4.onnx")); // The test's own model stand-in.
        var noModel = _service.Availability().Upscale!;
        Assert.Equal("no_model", noModel.Detail);
        Assert.Equal("no_model", noModel.Tiers["fast"]);
        var (s2, b2) = await SendAsync(HttpMethod.Post, Routes.ImageUpscales, token, JobBody(id));
        AssertError(s2, b2, HttpStatusCode.ServiceUnavailable, ErrorCodes.EngineMissing);
        Assert.Empty(_helper.Calls);
    }

    [Theory]
    [InlineData(1.0, "none")] // 8.2 s per image
    [InlineData(0.5, "slow")] // 16.4 s
    [InlineData(0.05, "very_slow")] // 163.8 s
    public async Task TheEstimateWarnsAboveTenAndAboveAHundredSecondsPerImage(double rate, string warning)
    {
        var token = await _host.GetTokenAsync();
        var id = await ImageJobAsync(token);
        _rate = rate;
        var (status, body) = await SendAsync(HttpMethod.Post, Routes.ImageUpscales + "/estimate", token, JobBody(id));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(warning, body.GetProperty("warning").GetString());
        Assert.True(body.GetProperty("available").GetBoolean());
        Assert.Empty(_helper.Calls); // Nothing queued.
    }

    [Fact]
    public void TheWarningThresholdsAreTenAndAHundred()
    {
        Assert.Equal("none", UpscaleWarnings.For(10));
        Assert.Equal("slow", UpscaleWarnings.For(10.1));
        Assert.Equal("slow", UpscaleWarnings.For(100));
        Assert.Equal("very_slow", UpscaleWarnings.For(100.1));
    }

    [Fact]
    public async Task CancelRemovesThePartialAndPublishesNothingNew()
    {
        var token = await _host.GetTokenAsync();
        var id = await ImageJobAsync(token);
        _helper.Hold = true;
        var (_, job) = await SendAsync(HttpMethod.Post, Routes.ImageUpscales, token, JobBody(id));
        var upId = job.GetProperty("id").GetString()!;
        await _helper.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(File.Exists(_helper.Calls[0].Output));

        var (status, ended) = await SendAsync(HttpMethod.Delete, $"{Routes.ImageUpscales}/{upId}", token);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(GenerationJobStates.Cancelled, ended.GetProperty("state").GetString());
        Assert.False(File.Exists(_helper.Calls[0].Output));
        Assert.Single(Directory.GetFiles(_output.FinishedFolder(ModelKinds.Image)));
    }
}
