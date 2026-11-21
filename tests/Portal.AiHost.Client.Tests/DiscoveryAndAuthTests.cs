using Portal.AiHost.Contracts;

namespace Portal.AiHost.Client.Tests;

public class DiscoveryTests
{
    private static string WriteDiscovery(string root, string pfn, StubHost host, string instanceId)
    {
        var dir = Path.Combine(root, pfn, "LocalState", "discovery");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "host.json");
        File.WriteAllText(file, AiHostJson.Serialize(new DiscoveryFile
        {
            BaseUrl = host.BaseUri.ToString().TrimEnd('/'),
            Port = host.Port,
            InstanceId = instanceId,
        }, AiHostJsonContext.Default.DiscoveryFile));
        return file;
    }

    private static AiHostClient FileClient(string root, int defaultPort) => AiHostClient.Create(new AiHostClientOptions
    {
        AppId = "com.test.app",
        AppDisplayName = "Test",
        TokenStore = new MemoryTokenStore(),
        Discovery = new HostDiscoveryOptions { PackagesRoot = root, PackageFamilyNames = ["Host_pfn"], DefaultPort = defaultPort },
    });

    [Fact]
    public async Task Override_wins_and_host_without_token_needs_access()
    {
        using var host = new StubHost();
        using var client = AiHostClient.Create(new AiHostClientOptions
        {
            AppId = "a.b",
            AppDisplayName = "T",
            TokenStore = new MemoryTokenStore(),
            DiscoveryOverride = host.BaseUri,
        });
        Assert.Equal(AiHostState.NeedsAccess, await client.CheckAsync());
        Assert.Equal(host.BaseUri, client.BaseAddress);
    }

    [Fact]
    public async Task Environment_variable_override_is_used()
    {
        using var host = new StubHost();
        var root = Path.Combine(Path.GetTempPath(), "pah-env-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(ApiInfo.UrlOverrideEnvironmentVariable, host.BaseUri.ToString());
        try
        {
            using var client = FileClient(root, StubHost.FreePort());
            Assert.Equal(AiHostState.NeedsAccess, await client.CheckAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable(ApiInfo.UrlOverrideEnvironmentVariable, null);
        }
    }

    [Fact]
    public async Task Discovery_file_is_used_when_instance_id_matches()
    {
        using var host = new StubHost { InstanceId = "abc" };
        var root = Path.Combine(Path.GetTempPath(), "pah-disc-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteDiscovery(root, "Host_pfn", host, "abc");
            using var client = FileClient(root, StubHost.FreePort());
            Assert.Equal(AiHostState.NeedsAccess, await client.CheckAsync());
            Assert.Equal(host.BaseUri, client.BaseAddress);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Discovery_file_with_wrong_instance_id_is_stale_and_ignored()
    {
        using var host = new StubHost { InstanceId = "real" };
        var root = Path.Combine(Path.GetTempPath(), "pah-stale-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteDiscovery(root, "Host_pfn", host, "other-instance");
            using var client = FileClient(root, StubHost.FreePort());
            Assert.Equal(AiHostState.NotRunning, await client.CheckAsync()); // package folder exists, nothing answers
            Assert.Null(client.BaseAddress);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Default_port_is_the_last_resort()
    {
        using var host = new StubHost();
        var root = Path.Combine(Path.GetTempPath(), "pah-def-" + Guid.NewGuid().ToString("N"));
        using var client = FileClient(root, host.Port);
        Assert.Equal(AiHostState.NeedsAccess, await client.CheckAsync());
    }

    [Fact]
    public async Task Nothing_found_and_no_package_means_not_installed()
    {
        var root = Path.Combine(Path.GetTempPath(), "pah-none-" + Guid.NewGuid().ToString("N"));
        using var client = FileClient(root, StubHost.FreePort());
        Assert.Equal(AiHostState.NotInstalled, await client.CheckAsync());
    }

    [Fact]
    public async Task A_server_that_is_not_the_host_is_rejected()
    {
        using var host = new StubHost();
        host.Handler = null;
        // a different service answering ping must not be accepted: reuse the stub but break its service name via a raw listener
        using var impostor = new System.Net.HttpListener();
        var port = StubHost.FreePort();
        impostor.Prefixes.Add($"http://127.0.0.1:{port}/");
        impostor.Start();
        _ = Task.Run(async () =>
        {
            var ctx = await impostor.GetContextAsync();
            await StubHost.WriteJsonAsync(ctx, 200, "{\"service\":\"something-else\"}");
        });
        using var client = AiHostClient.Create(new AiHostClientOptions
        {
            AppId = "a.b",
            AppDisplayName = "T",
            TokenStore = new MemoryTokenStore(),
            DiscoveryOverride = new Uri($"http://127.0.0.1:{port}/"),
            Discovery = new HostDiscoveryOptions { PackagesRoot = Path.Combine(Path.GetTempPath(), "pah-x-" + Guid.NewGuid().ToString("N")), DefaultPort = StubHost.FreePort() },
        });
        Assert.Equal(AiHostState.NotInstalled, await client.CheckAsync());
    }

    [Fact]
    public async Task TryStartHost_launches_then_waits_for_the_host()
    {
        var port = StubHost.FreePort();
        StubHost? host = null;
        try
        {
            using var client = AiHostClient.Create(new AiHostClientOptions
            {
                AppId = "a.b",
                AppDisplayName = "T",
                TokenStore = new MemoryTokenStore(),
                DiscoveryOverride = new Uri($"http://127.0.0.1:{port}/"),
                Discovery = new HostDiscoveryOptions { PackagesRoot = Path.Combine(Path.GetTempPath(), "pah-s-" + Guid.NewGuid().ToString("N")), DefaultPort = StubHost.FreePort(), PingTimeout = TimeSpan.FromMilliseconds(300) },
                HostStartWait = TimeSpan.FromSeconds(5),
                HostLauncher = launchToken =>
                {
                    // the "host" comes up shortly after the launch request
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(400);
                        host = new StubHost(port);
                    });
                    return Task.FromResult(true);
                },
            });
            Assert.Equal(AiHostState.NotInstalled, await client.CheckAsync());
            Assert.True(await client.TryStartHostAsync());
            Assert.Equal(AiHostState.NeedsAccess, client.State);
        }
        finally
        {
            host?.Dispose();
        }
    }
}

public class AuthTests
{
    private static Task<bool> AuthHandler(StubHost host, System.Net.HttpListenerContext ctx, Func<int, string> pollStatus,
        int[] polls)
    {
        var path = ctx.Request.Url!.AbsolutePath;
        if (path == "/host/auth/requests" && ctx.Request.HttpMethod == "POST")
        {
            return Reply(StubHost.WriteJsonAsync(ctx, 202, AiHostJson.Serialize(new AuthRequestCreated
            {
                RequestId = "ar_1",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                PollWaitMs = 100,
            }, AiHostJsonContext.Default.AuthRequestCreated)));
        }

        if (path == "/host/auth/requests/ar_1")
        {
            var n = Interlocked.Increment(ref polls[0]);
            var status = pollStatus(n);
            return Reply(StubHost.WriteJsonAsync(ctx, 200, AiHostJson.Serialize(new AuthRequestStatus
            {
                Status = status,
                Token = status == AuthStatuses.Approved ? "pah_new" : null,
                TokenId = "tk_1",
            }, AiHostJsonContext.Default.AuthRequestStatus)));
        }

        if (path == "/host/status")
        {
            return Reply(StubHost.WriteJsonAsync(ctx, 200, AiHostJson.Serialize(new HostStatus { State = HostStates.Ready },
                AiHostJsonContext.Default.HostStatus)));
        }

        return Task.FromResult(false);
    }

    private static async Task<bool> Reply(Task t)
    {
        await t;
        return true;
    }

    [Fact]
    public async Task Request_then_poll_until_approved_stores_the_token()
    {
        using var host = new StubHost();
        var polls = new[] { 0 };
        host.Handler = (ctx, _) => AuthHandler(host, ctx, n => n < 3 ? AuthStatuses.Pending : AuthStatuses.Approved, polls);
        var store = new MemoryTokenStore();
        using var client = TestKit.Client(host, store);
        var states = new List<AiHostState>();
        client.StateChanged += (_, e) => states.Add(e.NewState);

        var result = await client.RequestAccessAsync(wait: true);

        Assert.Equal(AuthStatuses.Approved, result.Status);
        Assert.Equal(AiHostState.Ready, result.State);
        Assert.Equal("pah_new", await store.LoadAsync(default));
        Assert.Equal("pah_new", client.GetBearerToken());
        Assert.Equal(3, polls[0]);
        Assert.Contains(AiHostState.Ready, states);
        var create = host.Requests.First(r => r.Method == "POST" && r.PathAndQuery == "/host/auth/requests");
        Assert.Contains("\"app_id\":\"com.test.app\"", create.Body);
        Assert.Contains("\"app_name\":\"Test App\"", create.Body);
        Assert.DoesNotContain(host.Requests, r => r.Headers.ContainsKey("Authorization")
            && r.PathAndQuery.StartsWith("/host/auth/requests", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Denied_sets_denied_state_and_stores_nothing()
    {
        using var host = new StubHost();
        host.Handler = (ctx, _) => AuthHandler(host, ctx, _ => AuthStatuses.Denied, new[] { 0 });
        var store = new MemoryTokenStore();
        using var client = TestKit.Client(host, store);
        var result = await client.RequestAccessAsync();
        Assert.Equal(AiHostState.Denied, result.State);
        Assert.Null(await store.LoadAsync(default));
    }

    [Fact]
    public async Task Access_denied_cooldown_error_maps_to_denied()
    {
        using var host = new StubHost();
        host.Handler = (ctx, _) => Reply(StubHost.WriteErrorAsync(ctx, ErrorCodes.AccessDenied));
        using var client = TestKit.Client(host, new MemoryTokenStore());
        Assert.Equal(AiHostState.Denied, (await client.RequestAccessAsync()).State);
    }

    [Fact]
    public async Task Without_wait_returns_pending_and_a_later_wait_continues_the_same_request()
    {
        using var host = new StubHost();
        var polls = new[] { 0 };
        host.Handler = (ctx, _) => AuthHandler(host, ctx, _ => AuthStatuses.Approved, polls);
        using var client = TestKit.Client(host, new MemoryTokenStore());

        var first = await client.RequestAccessAsync(wait: false);
        Assert.Equal(AuthStatuses.Pending, first.Status);
        Assert.Equal(AiHostState.NeedsAccess, first.State);

        var second = await client.RequestAccessAsync(wait: true);
        Assert.Equal(AuthStatuses.Approved, second.Status);
        Assert.Equal(1, host.Requests.Count(r => r.Method == "POST" && r.PathAndQuery == "/host/auth/requests"));
    }

    [Fact]
    public async Task Request_access_with_host_down_reports_unavailable_without_throwing()
    {
        using var client = TestKit.Client(null, new MemoryTokenStore());
        var result = await client.RequestAccessAsync();
        Assert.Equal("unavailable", result.Status);
    }

    [Fact]
    public async Task Forget_access_deletes_the_token_and_signs_out()
    {
        using var host = new StubHost();
        host.Handler = (ctx, _) =>
        {
            if (ctx.Request.HttpMethod == "DELETE" && ctx.Request.Url!.AbsolutePath == "/host/auth/token")
            {
                ctx.Response.StatusCode = 204;
                ctx.Response.Close();
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        };
        var store = new MemoryTokenStore(TestKit.Token);
        using var client = TestKit.Client(host, store);
        await client.ForgetAccessAsync();
        Assert.Null(await store.LoadAsync(default));
        Assert.Equal(AiHostState.NeedsAccess, client.State);
        Assert.Equal(1, host.Count("/host/auth/token"));
    }
}
