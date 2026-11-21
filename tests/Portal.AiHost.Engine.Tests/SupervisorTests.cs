using System.Diagnostics;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Engine;

namespace Portal.AiHost.Engine.Tests;

/// <summary>Adapter that launches the stub with the standard loopback arguments plus per-launch extras.</summary>
internal sealed class StubAdapter : IEngineAdapter
{
    private int _launches;
    public Func<int, string[]> Extra { get; set; } = _ => Array.Empty<string>();
    public int Launches => Volatile.Read(ref _launches);
    public string EngineKind => "stub";
    public string HealthPath => "/health";
    public string ChatPath => "/v1/chat/completions";
    public string EmbeddingsPath => "/v1/embeddings";
    public string TokenizePath => "/tokenize";
    public string ApplyTemplatePath => "/apply-template";

    public IReadOnlyList<string> BuildArgs(EngineLaunchSpec spec, EngineEndpoint ep)
    {
        var n = Interlocked.Increment(ref _launches);
        var args = new List<string> { "--host", "127.0.0.1", "--port", ep.Port.ToString(), "--api-key", ep.ApiKey };
        args.AddRange(Extra(n));
        return args;
    }
}

internal static class Stub
{
    public static string Exe => Path.Combine(AppContext.BaseDirectory, "StubChild.exe");

    public static EngineSupervisorOptions Options(
        Func<EngineLaunchSpec, bool, EngineBinary>? resolve = null, TimeSpan? idle = null, int maxCrashes = 3) => new()
    {
        ResolveBinary = resolve ?? ((_, _) => new EngineBinary("cpu", Exe)),
        HealthPollInterval = TimeSpan.FromMilliseconds(50),
        LoadTimeout = TimeSpan.FromSeconds(20),
        HeartbeatInterval = TimeSpan.FromMilliseconds(200),
        HeartbeatTimeout = TimeSpan.FromSeconds(1),
        RestartBackoff = new[] { TimeSpan.FromMilliseconds(50) },
        MaxCrashes = maxCrashes,
        IdleUnloadAfter = idle,
        KillWait = TimeSpan.FromSeconds(5),
    };

    public static EngineLaunchSpec Spec(string backend = "cpu") => new()
    {
        ModelId = "stub",
        ModelFiles = new[] { Path.Combine(Path.GetTempPath(), "does-not-matter.gguf") },
        Placement = new Placement { Backend = backend, GpuLayers = backend == "cpu" ? 0 : 10, ContextTokens = 1024 },
    };

    public static ChatCompletionRequest Chat() => new()
    {
        Messages = new List<ChatMessage> { new() { Role = ChatRoles.User, Content = new ChatContent { Text = "hi" } } },
    };

    public static async Task WaitFor(Func<bool> cond, int ms = 15000)
    {
        var sw = Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > ms)
            {
                throw new TimeoutException("Condition not met.");
            }

            await Task.Delay(25);
        }
    }

    public static bool Alive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

public class SupervisorTests
{
    private static EngineSupervisor Make(StubAdapter a, EngineSupervisorOptions? o = null) =>
        new(ModelKinds.Chat, a, o ?? Stub.Options());

    [Fact]
    public async Task StartsWaitsForHealthAndServesRequests()
    {
        var a = new StubAdapter { Extra = _ => new[] { "--load-ms", "400" } };
        await using var s = Make(a);
        var states = new List<EngineState>();
        s.StateChanged += (_, e) => states.Add(e.State);
        await s.StartAsync(Stub.Spec(), CancellationToken.None);

        Assert.Equal(EngineState.Ready, s.State);
        Assert.Equal(new[] { EngineState.Starting, EngineState.Loading, EngineState.Ready }, states);

        var chat = await s.ChatAsync(Stub.Chat(), CancellationToken.None);
        Assert.Equal("hello", chat.Choices[0].Message.Content);

        var emb = await s.EmbedAsync(new EmbeddingsRequest { Input = new StringOrList { Values = { "x" } } }, CancellationToken.None);
        Assert.Equal(2, emb.Data[0].Embedding.Values!.Length);

        Assert.Equal(3, await s.CountTokensAsync(new TokenizeRequest { Text = "a b c" }, CancellationToken.None));
        Assert.Equal(3, await s.CountTokensAsync(new TokenizeRequest { Messages = Stub.Chat().Messages }, CancellationToken.None));

        var pid = s.ProcessId!.Value;
        await s.StopAsync(CancellationToken.None);
        Assert.Equal(EngineState.Stopped, s.State);
        Assert.Null(s.Current);
        await Stub.WaitFor(() => !Stub.Alive(pid));
        var ex = await Assert.ThrowsAsync<EngineException>(() => s.ChatAsync(Stub.Chat(), CancellationToken.None));
        Assert.Equal(ErrorCodes.NoModel, ex.ErrorCode);
    }

    [Fact]
    public async Task RequestDuringLoadSaysModelLoading()
    {
        var a = new StubAdapter { Extra = _ => new[] { "--load-ms", "1500" } };
        await using var s = Make(a);
        var start = s.StartAsync(Stub.Spec(), CancellationToken.None);
        await Stub.WaitFor(() => s.State == EngineState.Loading);
        var ex = await Assert.ThrowsAsync<EngineException>(() => s.ChatAsync(Stub.Chat(), CancellationToken.None));
        Assert.Equal(ErrorCodes.ModelLoading, ex.ErrorCode);
        await start;
    }

    [Fact]
    public async Task StreamsChunksAndCancelFreesTheRequest()
    {
        await using var s = Make(new StubAdapter());
        await s.StartAsync(Stub.Spec(), CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var got = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var c in s.StreamChatAsync(Stub.Chat(), cts.Token))
            {
                Assert.StartsWith("tok", c.Choices[0].Delta.Content);
                if (++got == 2)
                {
                    cts.Cancel();
                }
            }
        });
        Assert.Equal(2, got);
        await Stub.WaitFor(() => s.ActiveRequests == 0, 3000);
        // engine still healthy after a cancel
        Assert.Equal("hello", (await s.ChatAsync(Stub.Chat(), CancellationToken.None)).Choices[0].Message.Content);
    }

    [Fact]
    public async Task CrashRestartsWithBackoffAndInFlightGetsCrashed()
    {
        var a = new StubAdapter { Extra = n => n == 1 ? new[] { "--crash-ms", "700" } : Array.Empty<string>() };
        await using var s = Make(a);
        await s.StartAsync(Stub.Spec(), CancellationToken.None);
        var firstPid = s.ProcessId!.Value;

        await Assert.ThrowsAsync<EngineCrashedException>(async () =>
        {
            await foreach (var _ in s.StreamChatAsync(Stub.Chat(), CancellationToken.None))
            {
            }
        });

        await Stub.WaitFor(() => s.State == EngineState.Ready && s.ProcessId != firstPid);
        Assert.Equal(2, a.Launches);
        Assert.Equal("hello", (await s.ChatAsync(Stub.Chat(), CancellationToken.None)).Choices[0].Message.Content);
    }

    [Fact]
    public async Task ThreeCrashesInWindowMeansFailed()
    {
        var a = new StubAdapter { Extra = _ => new[] { "--crash-ms", "1500" } };
        await using var s = Make(a);
        await s.StartAsync(Stub.Spec(), CancellationToken.None);
        await Stub.WaitFor(() => s.State == EngineState.Failed);
        Assert.Equal(3, a.Launches);
        var ex = await Assert.ThrowsAsync<EngineException>(() => s.ChatAsync(Stub.Chat(), CancellationToken.None));
        Assert.Equal(ErrorCodes.EngineError, ex.ErrorCode);
    }

    [Fact]
    public async Task ExitDuringLoadFailsStart()
    {
        var a = new StubAdapter { Extra = _ => new[] { "--exit-ms", "200", "--load-ms", "60000" } };
        await using var s = Make(a);
        var ex = await Assert.ThrowsAsync<EngineException>(() => s.StartAsync(Stub.Spec(), CancellationToken.None));
        Assert.Equal(ErrorCodes.EngineError, ex.ErrorCode);
        Assert.Equal(EngineState.Failed, s.State);
    }

    [Fact]
    public async Task VulkanCrashRestartsOnCpuAndMarksVulkanBad()
    {
        var a = new StubAdapter { Extra = n => n == 1 ? new[] { "--crash-ms", "500" } : Array.Empty<string>() };
        await using var s = Make(a, Stub.Options((_, forceCpu) => new EngineBinary(forceCpu ? "cpu" : "vulkan", Stub.Exe)));
        string? notice = null;
        s.BackendFallback += (_, m) => notice = m;
        await s.StartAsync(Stub.Spec("vulkan"), CancellationToken.None);
        Assert.Equal("vulkan", s.Current!.Placement.Backend);

        await Stub.WaitFor(() => s.VulkanMarkedBad);
        await Stub.WaitFor(() => s.State == EngineState.Ready && a.Launches == 2);
        Assert.Equal("cpu", s.Current!.Placement.Backend);
        Assert.Equal(0, s.Current.Placement.GpuLayers);
        Assert.NotNull(notice);
    }

    [Fact]
    public async Task IdleUnloadStopsTheEngine()
    {
        await using var s = Make(new StubAdapter(), Stub.Options(idle: TimeSpan.FromMilliseconds(500)));
        await s.StartAsync(Stub.Spec(), CancellationToken.None);
        var pid = s.ProcessId!.Value;
        await Stub.WaitFor(() => s.State == EngineState.Stopped, 8000);
        await Stub.WaitFor(() => !Stub.Alive(pid));
    }

    [Fact]
    public async Task CrashRestartWhileIdleKeepsTheIdleClock()
    {
        // An engine killed during the quiet period is restarted, but the restart is not use: it must still
        // unload on the clock of the last request, not a fresh idle period from the restart.
        var idle = TimeSpan.FromSeconds(4);
        var a = new StubAdapter();
        await using var s = Make(a, Stub.Options(idle: idle));
        await s.StartAsync(Stub.Spec(), CancellationToken.None);
        Assert.Equal("hello", (await s.ChatAsync(Stub.Chat(), CancellationToken.None)).Choices[0].Message.Content);
        var lastRequest = Stopwatch.StartNew();
        Assert.Equal(0, s.ActiveRequests);

        await Task.Delay(TimeSpan.FromSeconds(2.5));
        var firstPid = s.ProcessId!.Value;
        using (var p = Process.GetProcessById(firstPid))
        {
            p.Kill();
        }

        await Stub.WaitFor(() => a.Launches == 2 || s.State == EngineState.Stopped, 8000);
        await Stub.WaitFor(() => s.State == EngineState.Stopped, 10000);
        Assert.Equal(2, a.Launches);
        // Restarting the clock would put the unload at about 2.5 + 4 = 6.5 s; the last request's clock gives about 4-5 s.
        Assert.True(lastRequest.Elapsed < TimeSpan.FromSeconds(5.8), $"unloaded {lastRequest.Elapsed.TotalSeconds:F1}s after the last request");
    }

    [Fact]
    public async Task ForwardOnlyAllowsTheProxiedRoutes()
    {
        await using var s = Make(new StubAdapter());
        await s.StartAsync(Stub.Spec(), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<EngineException>(() => s.ForwardAsync(HttpMethod.Get, "/slots", null, CancellationToken.None));
        Assert.Equal(ErrorCodes.NotFound, ex.ErrorCode);

        using var ok = await s.ForwardAsync(HttpMethod.Post, "/tokenize",
            new StringContent("{\"content\":\"x\"}", System.Text.Encoding.UTF8, "application/json"), CancellationToken.None);
        Assert.Equal(System.Net.HttpStatusCode.OK, ok.Response.StatusCode);
    }

    [Fact]
    public async Task DisposeKillsTheChild()
    {
        var s = Make(new StubAdapter());
        await s.StartAsync(Stub.Spec(), CancellationToken.None);
        var pid = s.ProcessId!.Value;
        await s.DisposeAsync();
        await Stub.WaitFor(() => !Stub.Alive(pid));
    }
}

public class JobObjectTests
{
    [Fact]
    public async Task ChildDiesWhenParentIsKilled()
    {
        var psi = new ProcessStartInfo(Stub.Exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add("host");
        psi.ArgumentList.Add(Stub.Exe);
        psi.ArgumentList.Add("--port");
        var tl = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); tl.Start(); var fp = ((System.Net.IPEndPoint)tl.LocalEndpoint).Port; tl.Stop(); psi.ArgumentList.Add(fp.ToString());
        using var host = Process.Start(psi)!;
        var line = await host.StandardOutput.ReadLineAsync();
        Assert.StartsWith("pid=", line);
        var childPid = int.Parse(line!["pid=".Length..]);
        Assert.True(Stub.Alive(childPid));

        host.Kill(); // simulates a host crash: no cleanup code runs
        await host.WaitForExitAsync();
        await Stub.WaitFor(() => !Stub.Alive(childPid), 10000);
    }
}
