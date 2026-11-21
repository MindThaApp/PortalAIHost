using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Engine;

namespace Portal.AiHost.Engine.Tests;

/// <summary>
/// Real llama-server tests. They skip unless the CPU build (scripts/fetch-engine.ps1) and a tiny
/// GGUF (%TEMP%\portalaihost-wp2\model.gguf, 300 MB or less) are present.
/// </summary>
public class IntegrationTests
{
    private static readonly string Work = Path.Combine(Path.GetTempPath(), "portalaihost-wp2");
    private static string Model => Path.Combine(Work, "model.gguf");

    private static string? FindEngine()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var exe = Path.Combine(dir.FullName, "engine", "cpu", "llama-server.exe");
            if (File.Exists(exe))
            {
                return exe;
            }
        }

        return null;
    }

    private static string RequireEngine()
    {
        var exe = FindEngine();
        Assert.SkipUnless(exe != null && File.Exists(Model), "llama-server CPU build or test model missing.");
        return exe!;
    }

    private static EngineLaunchSpec Spec() => new()
    {
        ModelId = "tiny",
        ModelFiles = new[] { Model },
        Placement = new Placement { Backend = "cpu", ContextTokens = 4096 },
    };

    [Fact]
    public async Task LoadStreamCancelUnload()
    {
        var exe = RequireEngine();
        var info = GgufReader.Read(Model);
        Assert.False(string.IsNullOrEmpty(info.Architecture));
        Assert.True(info.BlockCount > 0);

        await using var s = new EngineSupervisor(ModelKinds.Chat, new LlamaServerAdapter(),
            new EngineSupervisorOptions { ResolveBinary = (_, _) => new EngineBinary("cpu", exe) });
        await s.StartAsync(Spec(), CancellationToken.None);
        Assert.Equal(EngineState.Ready, s.State);
        var pid = s.ProcessId!.Value;

        var req = new ChatCompletionRequest
        {
            MaxTokens = 3000,
            Messages = new List<ChatMessage> { new() { Role = ChatRoles.User, Content = new ChatContent { Text = "Write a long story." } } },
        };
        using var cts = new CancellationTokenSource();
        var chunks = 0;
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var c in s.StreamChatAsync(req, cts.Token))
            {
                if (++chunks == 5)
                {
                    cts.Cancel();
                }
            }
        });
        Assert.True(chunks >= 5);
        Assert.True(s.ActiveRequests == 0);

        var small = new ChatCompletionRequest
        {
            MaxTokens = 8,
            Messages = new List<ChatMessage> { new() { Role = ChatRoles.User, Content = new ChatContent { Text = "Say hi." } } },
        };
        var reply = await s.ChatAsync(small, CancellationToken.None);
        Assert.NotEmpty(reply.Choices);

        Assert.True(await s.CountTokensAsync(new TokenizeRequest { Text = "Hello there, world." }, CancellationToken.None) > 2);

        await s.StopAsync(CancellationToken.None);
        Assert.Equal(EngineState.Stopped, s.State);
        await Stub.WaitFor(() => !Stub.Alive(pid), 10000);
    }

    /// <summary>
    /// The architecture's open check: does llama-server free its slot when the client disconnects,
    /// for streaming and for non-streaming requests? Result is written to cancel-result.txt.
    /// </summary>
    [Fact]
    public async Task CancelOnDisconnect_StreamAndNonStream()
    {
        var exe = RequireEngine();
        var results = new StringBuilder();
        foreach (var stream in new[] { true, false })
        {
            var ep = new EngineEndpoint(GetPort(), "k" + Guid.NewGuid().ToString("N"));
            var args = LlamaServerArgs.Build(Spec(), ep, new LlamaServerArgsOptions { ExtraArgs = new[] { "--slots" } });
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var proc = Process.Start(psi)!;
            proc.OutputDataReceived += (_, _) => { };
            proc.ErrorDataReceived += (_, _) => { };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            try
            {
                using var http = new HttpClient { BaseAddress = ep.BaseAddress, Timeout = Timeout.InfiniteTimeSpan };
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ep.ApiKey);
                await Stub.WaitFor(() => Ready(http).GetAwaiter().GetResult(), 60000);

                var body = "{\"messages\":[{\"role\":\"user\",\"content\":\"Write a very long story.\"}],\"max_tokens\":3500,\"ignore_eos\":true,\"stream\":" + (stream ? "true" : "false") + "}";
                using var cts = new CancellationTokenSource();
                var call = Task.Run(async () =>
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                    using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    if (stream)
                    {
                        using var rs = await resp.Content.ReadAsStreamAsync(cts.Token);
                        var buf = new byte[4096];
                        while (await rs.ReadAsync(buf, cts.Token) > 0)
                        {
                        }
                    }
                    else
                    {
                        await resp.Content.ReadAsStringAsync(cts.Token);
                    }
                });

                await Stub.WaitFor(() => Busy(http).GetAwaiter().GetResult(), 20000);
                await Task.Delay(1000);
                Assert.True(await Busy(http), "generation should still be running before the disconnect");
                var t0 = Stopwatch.StartNew();
                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);

                double? freedMs = null;
                while (t0.ElapsedMilliseconds < 15000)
                {
                    if (!await Busy(http))
                    {
                        freedMs = t0.Elapsed.TotalMilliseconds;
                        break;
                    }

                    await Task.Delay(50);
                }

                results.AppendLine(stream
                    ? $"stream: slot freed after {(freedMs.HasValue ? freedMs.Value.ToString("F0") + " ms" : "NOT within 15 s")}"
                    : $"non-stream: slot freed after {(freedMs.HasValue ? freedMs.Value.ToString("F0") + " ms" : "NOT within 15 s")}");
                if (stream)
                {
                    Assert.True(freedMs is < 1500, "streaming request must stop within about 1 s of the disconnect");
                }
            }
            finally
            {
                if (!proc.HasExited)
                {
                    proc.Kill(true);
                }
            }
        }

        File.WriteAllText(Path.Combine(Work, "cancel-result.txt"), results.ToString());
    }

    private static async Task<bool> Ready(HttpClient http)
    {
        try
        {
            using var r = await http.GetAsync("/health");
            return r.StatusCode == HttpStatusCode.OK;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private static async Task<bool> Busy(HttpClient http)
    {
        using var r = await http.GetAsync("/slots");
        r.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        foreach (var slot in doc.RootElement.EnumerateArray())
        {
            if (slot.TryGetProperty("is_processing", out var p) && p.GetBoolean())
            {
                return true;
            }
        }

        return false;
    }

    private static int GetPort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
