using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using Portal.AiHost.Contracts;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Portal.AiHost.Client.Tests;

/// <summary>One request the stub host saw.</summary>
public sealed record SeenRequest(string Method, string PathAndQuery, IReadOnlyDictionary<string, string> Headers, string Body);

/// <summary>An in-process host on a loopback port. Answers <c>/host/ping</c> itself; tests supply the rest.</summary>
public sealed class StubHost : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();

    public StubHost(int? port = null)
    {
        Port = port ?? FreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    public int Port { get; }

    public string InstanceId { get; set; } = "inst-0001";

    public Uri BaseUri => new($"http://127.0.0.1:{Port}/");

    public ConcurrentQueue<SeenRequest> Requests { get; } = new();

    /// <summary>Handles a request; return false to get a 404.</summary>
    public Func<HttpListenerContext, string, Task<bool>>? Handler { get; set; }

    public int Count(string pathPrefix) => Requests.Count(r => r.PathAndQuery.StartsWith(pathPrefix, StringComparison.Ordinal));

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public static async Task WriteJsonAsync(HttpListenerContext ctx, int status, string json,
        IDictionary<string, string>? headers = null)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        if (headers is not null)
        {
            foreach (var (k, v) in headers)
            {
                ctx.Response.Headers[k] = v;
            }
        }

        var bytes = Encoding.UTF8.GetBytes(json);
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    public static Task WriteErrorAsync(HttpListenerContext ctx, string code, int? retryAfterMs = null)
    {
        var info = ErrorCatalog.Get(code);
        var json = AiHostJson.Serialize(ErrorCatalog.Create(code, "stub " + code, retryAfterMs: retryAfterMs),
            AiHostJsonContext.Default.ApiError);
        return WriteJsonAsync(ctx, info.HttpStatus, json);
    }

    public static async Task WriteMoreAsync(HttpListenerContext ctx, string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n\n");
        await ctx.Response.OutputStream.WriteAsync(bytes);
        await ctx.Response.OutputStream.FlushAsync();
    }

    public static async Task WriteLinesAsync(HttpListenerContext ctx, params string[] lines)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.SendChunked = true;
        foreach (var line in lines)
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n\n");
            await ctx.Response.OutputStream.WriteAsync(bytes);
            await ctx.Response.OutputStream.FlushAsync();
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            string body;
            using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync();
            }

            var headers = ctx.Request.Headers.AllKeys.Where(k => k is not null)
                .ToDictionary(k => k!, k => ctx.Request.Headers[k] ?? "", StringComparer.OrdinalIgnoreCase);
            var path = ctx.Request.Url!.PathAndQuery;
            Requests.Enqueue(new SeenRequest(ctx.Request.HttpMethod, path, headers, body));

            if (path == "/host/ping")
            {
                await WriteJsonAsync(ctx, 200, AiHostJson.Serialize(
                    new PingResponse { HostVersion = "1.0.0", InstanceId = InstanceId }, AiHostJsonContext.Default.PingResponse));
                return;
            }

            if (Handler is null || !await Handler(ctx, body))
            {
                await WriteErrorAsync(ctx, ErrorCodes.NotFound);
            }
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
        {
            // client went away
        }
    }
}

public sealed class FakeFallback : IAiFallback
{
    public int ChatCalls { get; private set; }

    public int EmbedCalls { get; private set; }

    public string ModelId => "local-model";

    public bool SupportsEmbeddings { get; init; }

    public Task<string> ChatAsync(ChatCompletionRequest request, CancellationToken cancellationToken)
    {
        ChatCalls++;
        return Task.FromResult("local answer");
    }

    public async IAsyncEnumerable<string> StreamChatAsync(ChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ChatCalls++;
        await Task.Yield();
        yield return "local ";
        yield return "stream";
    }

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken)
    {
        EmbedCalls++;
        return Task.FromResult<IReadOnlyList<float[]>>(inputs.Select(_ => new[] { 9f }).ToList());
    }
}

public static class TestKit
{
    public const string Token = "pah_testtoken";

    public static ChatCompletionRequest Chat(string text = "hi") => new()
    {
        Messages = [new ChatMessage { Role = ChatRoles.User, Content = new ChatContent { Text = text } }],
    };

    public static string ChatJson(string text) => AiHostJson.Serialize(new ChatCompletionResponse
    {
        Id = "c1",
        Model = "m1",
        Choices = [new ChatChoice { Message = new ChatResponseMessage { Content = text }, FinishReason = "stop" }],
    }, AiHostJsonContext.Default.ChatCompletionResponse);

    public static string Chunk(string? text, string? finish = null) => "data: " + AiHostJson.Serialize(new ChatCompletionChunk
    {
        Id = "c1",
        Model = "m1",
        Choices = [new ChatChunkChoice { Delta = new ChatDeltaMessage { Content = text }, FinishReason = finish }],
    }, AiHostJsonContext.Default.ChatCompletionChunk);

    public static AiHostClientOptions Options(StubHost? host, ITokenStore? store = null, IAiFallback? fallback = null,
        bool embedFallback = false, int? maxAttempts = null, List<string>? log = null)
    {
        var emptyRoot = Path.Combine(Path.GetTempPath(), "pah-client-tests-empty-" + Guid.NewGuid().ToString("N"));
        return new AiHostClientOptions
        {
            AppId = "com.test.app",
            AppDisplayName = "Test App",
            TokenStore = store ?? new MemoryTokenStore(Token),
            Fallback = fallback,
            AllowEmbeddingFallback = embedFallback,
            DiscoveryOverride = host?.BaseUri ?? new Uri($"http://127.0.0.1:{StubHost.FreePort()}/"),
            Discovery = new HostDiscoveryOptions
            {
                PackagesRoot = emptyRoot,
                DefaultPort = StubHost.FreePort(),
                PingTimeout = TimeSpan.FromSeconds(2),
            },
            Retry = new RetryOptions
            {
                MaxAttempts = maxAttempts ?? 3,
                InitialDelay = TimeSpan.FromMilliseconds(1),
                MaxDelay = TimeSpan.FromMilliseconds(5),
            },
            Logger = log is null ? null : log.Add,
        };
    }

    public static AiHostClient Client(StubHost? host, ITokenStore? store = null, IAiFallback? fallback = null,
        bool embedFallback = false, int? maxAttempts = null) =>
        AiHostClient.Create(Options(host, store, fallback, embedFallback, maxAttempts));
}
