using System.Net;
using Portal.AiHost.Contracts;

namespace Portal.AiHost.Client.Tests;

public class VoiceCallTests
{
    private static Task<bool> Done(Task t) => t.ContinueWith(_ => true, TaskScheduler.Default);

    private static string Job(string status, double progress) =>
        $$"""{ "id": "sp_1", "status": "{{status}}", "progress": {{progress}}, "created_at": "2026-11-27T10:00:00Z" }""";

    [Fact]
    public async Task CreateVoice_sends_consent_and_the_sample_as_base64()
    {
        using var host = new StubHost();
        host.Handler = (ctx, _) => Done(StubHost.WriteJsonAsync(ctx, 201,
            """{ "id": "vc_a", "name": "Anna", "consent": "own_voice", "created_at": "2026-11-27T10:00:00Z", "created_by": "com.test.app", "duration_ms": 12000, "sample_rate": 24000 }"""));
        using var client = TestKit.Client(host);

        var voice = await client.CreateVoiceAsync("Anna", [1, 2, 3], VoiceConsent.OwnVoice);

        Assert.Equal("vc_a", voice.Id);
        var seen = host.Requests.Single(r => r.PathAndQuery == "/host/voices");
        Assert.Equal("POST", seen.Method);
        Assert.Equal("Bearer " + TestKit.Token, seen.Headers["Authorization"]);
        Assert.Contains("\"consent\":\"own_voice\"", seen.Body);
        Assert.Contains("\"sample_base64\":\"AQID\"", seen.Body);
    }

    [Fact]
    public async Task ConsentError_surfaces_its_code()
    {
        using var host = new StubHost();
        host.Handler = (ctx, _) => Done(StubHost.WriteErrorAsync(ctx, ErrorCodes.ConsentRequired));
        using var client = TestKit.Client(host);

        var ex = await Assert.ThrowsAsync<AiHostException>(() => client.CreateVoiceAsync("Anna", [1], "x"));
        Assert.Equal(ErrorCodes.ConsentRequired, ex.Code);
    }

    [Fact]
    public async Task Rename_and_delete_use_patch_and_delete()
    {
        using var host = new StubHost();
        host.Handler = (ctx, _) => ctx.Request.HttpMethod == "DELETE"
            ? Done(StubHost.WriteJsonAsync(ctx, 204, ""))
            : Done(StubHost.WriteJsonAsync(ctx, 200, """{ "id": "vc_a", "name": "New", "consent": "own_voice" }"""));
        using var client = TestKit.Client(host);

        Assert.Equal("New", (await client.RenameVoiceAsync("vc_a", "New")).Name);
        await client.DeleteVoiceAsync("vc_a");

        Assert.Contains(host.Requests, r => r.Method == "PATCH" && r.PathAndQuery == "/host/voices/vc_a");
        Assert.Contains(host.Requests, r => r.Method == "DELETE" && r.PathAndQuery == "/host/voices/vc_a");
    }

    [Fact]
    public async Task Speak_polls_until_completed_then_downloads_the_audio()
    {
        using var host = new StubHost();
        var polls = 0;
        host.Handler = async (ctx, _) =>
        {
            var path = ctx.Request.Url!.AbsolutePath;
            if (ctx.Request.HttpMethod == "POST")
            {
                await StubHost.WriteJsonAsync(ctx, 202, Job("queued", 0));
            }
            else if (path.EndsWith("/audio", StringComparison.Ordinal))
            {
                ctx.Response.ContentType = "audio/wav";
                await ctx.Response.OutputStream.WriteAsync(new byte[] { 9, 8, 7 });
                ctx.Response.Close();
            }
            else
            {
                await StubHost.WriteJsonAsync(ctx, 200, Job(++polls < 2 ? "running" : "completed", polls < 2 ? 0.5 : 1));
            }

            return true;
        };
        using var client = TestKit.Client(host);
        var seen = new List<double>();

        var bytes = await client.SpeakAsync(new SpeechJobRequest { BuiltinVoice = "af_heart", Text = "Hi." },
            new Progress<double>(seen.Add), TimeSpan.FromMilliseconds(10));

        Assert.Equal(new byte[] { 9, 8, 7 }, bytes);
        Assert.Contains(host.Requests, r => r.PathAndQuery == "/host/speech/jobs/sp_1/audio");
        var post = host.Requests.First(r => r.Method == "POST");
        Assert.Contains("\"builtin_voice\":\"af_heart\"", post.Body);
    }

    [Fact]
    public async Task Speak_throws_with_the_job_error_when_it_fails()
    {
        using var host = new StubHost();
        host.Handler = async (ctx, _) =>
        {
            await StubHost.WriteJsonAsync(ctx, ctx.Request.HttpMethod == "POST" ? 202 : 200,
                """{ "id": "sp_1", "status": "failed", "progress": 0.2, "created_at": "2026-11-27T10:00:00Z", "error": { "code": "engine_error", "message": "boom" } }""");
            return true;
        };
        using var client = TestKit.Client(host);

        var ex = await Assert.ThrowsAsync<AiHostException>(() =>
            client.SpeakAsync(new SpeechJobRequest { BuiltinVoice = "af_heart", Text = "Hi." }, null, TimeSpan.FromMilliseconds(10)));
        Assert.Equal(ErrorCodes.EngineError, ex.Code);
    }

    [Fact]
    public async Task VoiceCapabilities_are_read_from_the_capabilities_answer()
    {
        using var host = new StubHost();
        host.Handler = (ctx, _) => Done(StubHost.WriteJsonAsync(ctx, 200,
            """{ "api_version": "1", "voice": { "cloning": false, "speech": false, "formats": [], "reason": "no_model" } }"""));
        using var client = TestKit.Client(host);

        var voice = await client.GetVoiceCapabilitiesAsync();
        Assert.NotNull(voice);
        Assert.False(voice!.Speech);
        Assert.Equal("no_model", voice.Reason);
    }
}
