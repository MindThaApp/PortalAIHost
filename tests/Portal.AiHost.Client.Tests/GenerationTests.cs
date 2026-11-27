using System.Net;
using Portal.AiHost.Contracts;

namespace Portal.AiHost.Client.Tests;

public sealed class GenerationTests
{
    private static Task<bool> Done(Task t) => t.ContinueWith(_ => true, TaskScheduler.Default);

    private const string JobId = "gen_6f1c2a9e4b7d4c0f9a51d3e8b2c47a10";

    private static string Job(string state, string kind = "image") => AiHostJson.Serialize(new GenerationJob
    {
        Id = JobId,
        Kind = kind,
        Model = "sd-1.5-q8",
        State = state,
        Progress = state == GenerationJobStates.Succeeded ? 1 : 0.5,
        CreatedAt = DateTimeOffset.UtcNow,
        Result = state == GenerationJobStates.Succeeded
            ? new GenerationResultInfo { Url = $"/host/{kind}/jobs/{JobId}/result", ContentType = "image/png", Bytes = 5, Seed = 42 }
            : null,
    }, AiHostJsonContext.Default.GenerationJob);

    [Fact]
    public async Task Create_posts_the_request_with_the_token_and_reads_the_job()
    {
        using var host = new StubHost();
        host.Handler = (ctx, _) => ctx.Request.HttpMethod == "POST" && ctx.Request.Url!.AbsolutePath == "/host/image/jobs"
            ? Done(StubHost.WriteJsonAsync(ctx, 202, Job(GenerationJobStates.Queued)))
            : Task.FromResult(false);
        using var client = TestKit.Client(host);

        var job = await client.CreateImageJobAsync(new GenerationJobRequest { Prompt = "a lighthouse", Width = 512, Height = 512, Seed = 42 });
        Assert.Equal(JobId, job.Id);
        Assert.Equal(GenerationJobStates.Queued, job.State);
        var seen = host.Requests.Single(r => r.PathAndQuery == "/host/image/jobs");
        Assert.Equal("Bearer " + TestKit.Token, seen.Headers["Authorization"]);
        Assert.Contains("\"prompt\":\"a lighthouse\"", seen.Body, StringComparison.Ordinal);
        Assert.Contains("\"seed\":42", seen.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("frames", seen.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_cancel_wait_and_download_use_the_kind_routes()
    {
        using var host = new StubHost();
        var polls = 0;
        host.Handler = (ctx, _) =>
        {
            var path = ctx.Request.Url!.AbsolutePath;
            if (ctx.Request.HttpMethod == "DELETE" && path == $"/host/video/jobs/{JobId}")
            {
                return Done(StubHost.WriteJsonAsync(ctx, 200, Job(GenerationJobStates.Cancelled, "video")));
            }

            if (path == $"/host/video/jobs/{JobId}")
            {
                var state = Interlocked.Increment(ref polls) < 3 ? GenerationJobStates.Running : GenerationJobStates.Succeeded;
                return Done(StubHost.WriteJsonAsync(ctx, 200, Job(state, "video")));
            }

            if (path == $"/host/video/jobs/{JobId}/result")
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "video/x-msvideo";
                return Done(ctx.Response.OutputStream.WriteAsync("RIFF!"u8.ToArray()).AsTask()
                    .ContinueWith(_ => ctx.Response.Close(), TaskScheduler.Default));
            }

            return Task.FromResult(false);
        };
        using var client = TestKit.Client(host);

        var seen = new List<string>();
        var done = await client.WaitForGenerationJobAsync(ModelKinds.Video, JobId, TimeSpan.FromMilliseconds(10),
            new SyncProgress(j => seen.Add(j.State)));
        Assert.Equal(GenerationJobStates.Succeeded, done.State);
        Assert.Equal([GenerationJobStates.Running, GenerationJobStates.Running, GenerationJobStates.Succeeded], seen);

        using var file = new MemoryStream();
        Assert.Equal(5, await client.DownloadGenerationResultAsync(ModelKinds.Video, JobId, file));
        Assert.Equal("RIFF!"u8.ToArray(), file.ToArray());

        var cancelled = await client.CancelGenerationJobAsync(ModelKinds.Video, JobId);
        Assert.Equal(GenerationJobStates.Cancelled, cancelled.State);
    }

    [Fact]
    public async Task Host_errors_surface_with_their_codes()
    {
        using var host = new StubHost();
        host.Handler = (ctx, _) => ctx.Request.Url!.AbsolutePath switch
        {
            "/host/image/jobs" => Done(StubHost.WriteErrorAsync(ctx, ErrorCodes.EngineMissing)),
            "/host/video/jobs" => Done(StubHost.WriteErrorAsync(ctx, ErrorCodes.ModelNotInstalled)),
            _ => Done(StubHost.WriteErrorAsync(ctx, ErrorCodes.NotFound)),
        };
        using var client = TestKit.Client(host);

        var missing = await Assert.ThrowsAsync<AiHostException>(() => client.CreateImageJobAsync(new GenerationJobRequest { Prompt = "x" }));
        Assert.Equal(ErrorCodes.EngineMissing, missing.Code);
        Assert.Equal((int)HttpStatusCode.ServiceUnavailable, missing.HttpStatus);
        var notInstalled = await Assert.ThrowsAsync<AiHostException>(() => client.CreateVideoJobAsync(new GenerationJobRequest { Prompt = "x" }));
        Assert.Equal(ErrorCodes.ModelNotInstalled, notInstalled.Code);
        Assert.Null(await client.GetGenerationPreviewAsync(ModelKinds.Image, JobId));
    }

    [Fact]
    public async Task Bad_kinds_and_ids_are_refused_without_a_request()
    {
        using var host = new StubHost();
        using var client = TestKit.Client(host);
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetGenerationJobAsync(ModelKinds.Chat, JobId));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetGenerationJobAsync(ModelKinds.Image, "../../host/status"));
        Assert.Equal(0, host.Count("/host/image"));
    }

    private sealed class SyncProgress(Action<GenerationJob> report) : IProgress<GenerationJob>
    {
        public void Report(GenerationJob value) => report(value);
    }
}
