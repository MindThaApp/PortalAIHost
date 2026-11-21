using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Auth;
using Portal.AiHost.Core.Runtime;

namespace Portal.AiHost.Server;

public sealed class AiHostServerOptions
{
    /// <summary>Test mode uses ports 41890-41899 and never writes the real discovery file.</summary>
    public bool TestMode { get; init; }

    /// <summary>Overrides the port list (tests). Port 0 means OS-assigned.</summary>
    public IReadOnlyList<int>? Ports { get; init; }

    /// <summary>Where to write host.json; null writes none.</summary>
    public string? DiscoveryPath { get; init; }

    /// <summary>Maps each TCP connection to the calling process (Platform.Windows in the host).</summary>
    public ICallerIdentityResolver? CallerResolver { get; init; }

    public int MaxRequestBytes { get; init; } = ApiLimits.MaxRequestBytes;

    public const int FirstPort = ApiInfo.DefaultPort;
    public const int FirstTestPort = 41890;
    public const int PortRange = 10;

    public IReadOnlyList<int> CandidatePorts()
    {
        if (Ports is { Count: > 0 } p)
        {
            return p;
        }

        var first = TestMode ? FirstTestPort : FirstPort;
        var list = Enumerable.Range(first, PortRange).ToList();
        list.Add(0);
        return list;
    }
}

/// <summary>
/// Loopback HTTP listener: Kestrel slim builder, one terminal handler, no routing or reflection. Every request
/// passes the Host and Origin guards and the streaming body limit, gets its caller identity, then goes to
/// <see cref="AiHostRuntime.Pipeline"/>. There are no admin routes.
/// </summary>
public sealed class AiHostServer : IAsyncDisposable
{
    private readonly AiHostRuntime _runtime;
    private readonly AiHostServerOptions _options;
    private WebApplication? _app;

    /// <summary>Open TCP connections allowed at once.</summary>
    public const int MaxConnections = 256;

    /// <summary>How long a stop waits for open requests before closing them.</summary>
    public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

    private AiHostServer(AiHostRuntime runtime, AiHostServerOptions options)
    {
        _runtime = runtime;
        _options = options;
    }

    public int Port { get; private set; }

    public bool ListensOnIpv6 { get; private set; }

    public string BaseUrl => "http://127.0.0.1:" + Port.ToString(CultureInfo.InvariantCulture);

    public DateTimeOffset StartedAt { get; private set; }

    /// <summary>Every address the listener really bound (tests check they are all loopback).</summary>
    public IReadOnlyList<string> BoundAddresses =>
        _app?.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.ToList() ?? [];

    public static async Task<AiHostServer> StartAsync(AiHostRuntime runtime, AiHostServerOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(options);
        var server = new AiHostServer(runtime, options);
        Exception? last = null;
        foreach (var port in options.CandidatePorts())
        {
            // Fixed ports listen on both loopbacks or not at all: if [::1]:P is taken the next port is tried, so
            // a client that resolves localhost to ::1 never reaches a stranger. The OS-assigned fallback is IPv4
            // only, because the two families would get different ports.
            foreach (var withIpv6 in port != 0 && Socket.OSSupportsIPv6 ? new[] { true } : [false])
            {
                ct.ThrowIfCancellationRequested();
                var app = server.Build(port, withIpv6);
                try
                {
                    await app.StartAsync(ct).ConfigureAwait(false);
                    server._app = app;
                    server.Port = port != 0 ? port : BoundPort(app);
                    server.ListensOnIpv6 = withIpv6;
                    server.StartedAt = DateTimeOffset.UtcNow;
                    server.WriteDiscovery();
                    return server;
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or SocketException)
                {
                    last = ex;
                    await app.DisposeAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await app.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
        }

        throw new IOException("No loopback port was free for Portal AI Host.", last);
    }

    private static int BoundPort(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        foreach (var a in addresses ?? [])
        {
            if (Uri.TryCreate(a, UriKind.Absolute, out var uri) && uri.Port > 0)
            {
                return uri.Port;
            }
        }

        throw new InvalidOperationException("The listener has no bound port.");
    }

    private WebApplication Build(int port, bool withIpv6)
    {
        // Empty builder: no appsettings*.json, no environment variables, no command line, so nothing outside this
        // code can add a listener (for example Kestrel__Endpoints__X__Url=http://0.0.0.0:...). Kestrel core only,
        // without its configuration binding.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            Args = [],
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(); // Only what this code sets.
        builder.WebHost.UseKestrelCore();
        builder.Services.Configure<Microsoft.Extensions.Hosting.HostOptions>(o => o.ShutdownTimeout = ShutdownTimeout);
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            // Local apps only: a sane cap stops a runaway client from exhausting the host.
            k.Limits.MaxConcurrentConnections = MaxConnections;
            // Kestrel's own cap is a backstop one byte above ours, so our OpenAI-shaped 413 wins.
            k.Limits.MaxRequestBodySize = (long)_options.MaxRequestBytes + 1;
            k.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
            k.Listen(IPAddress.Loopback, port);
            if (withIpv6)
            {
                k.Listen(IPAddress.IPv6Loopback, port);
            }
        });
        var app = builder.Build();
        app.Run(HandleAsync);
        return app;
    }

    private async Task HandleAsync(HttpContext http)
    {
        var request = http.Request;
        if (!RequestGuard.IsLoopbackConnection(http.Connection.RemoteIpAddress, http.Connection.LocalIpAddress))
        {
            http.Abort();
            return;
        }

        if (!RequestGuard.IsAllowedHost(request.Headers.Host.ToString(), Port))
        {
            await WriteErrorAsync(http, ErrorCodes.ForbiddenOrigin, "The Host header is not allowed.").ConfigureAwait(false);
            return;
        }

        if (RequestGuard.IsBrowserRequest(request))
        {
            await WriteErrorAsync(http, ErrorCodes.ForbiddenOrigin, "Browser requests are not accepted.").ConfigureAwait(false);
            return;
        }

        RequestGuard.BodyResult result;
        byte[] body;
        try
        {
            (result, body) = await RequestGuard.ReadBodyAsync(request, _options.MaxRequestBytes, http.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (http.RequestAborted.IsCancellationRequested &&
            ex is IOException or OperationCanceledException or BadHttpRequestException)
        {
            return; // The client went away mid-body: nobody to answer.
        }
        catch (BadHttpRequestException)
        {
            http.Response.Headers.Connection = "close";
            await WriteErrorAsync(http, ErrorCodes.InvalidRequest, "The request body could not be read.").ConfigureAwait(false);
            return;
        }
        catch (IOException)
        {
            http.Response.Headers.Connection = "close";
            await WriteErrorAsync(http, ErrorCodes.InvalidRequest, "The request body could not be read.").ConfigureAwait(false);
            return;
        }
        if (result == RequestGuard.BodyResult.TooLarge)
        {
            http.Response.Headers.Connection = "close";
            await WriteErrorAsync(http, ErrorCodes.RequestTooLarge,
                $"The request body is larger than {_options.MaxRequestBytes} bytes.").ConfigureAwait(false);
            return;
        }

        var apiRequest = new ApiRequest
        {
            Method = request.Method,
            Path = request.Path.HasValue ? request.Path.Value! : "/",
            Query = ReadQuery(request),
            Headers = ReadHeaders(request),
            Body = body,
            Caller = ResolveCaller(http),
        };
        await _runtime.Pipeline.HandleAsync(apiRequest, new KestrelResponseSink(http)).ConfigureAwait(false);
    }

    private static readonly object CallerKey = new();

    private CallerIdentity? ResolveCaller(HttpContext http)
    {
        var c = http.Connection;
        if (_options.CallerResolver is null || c.RemoteIpAddress is null || c.LocalIpAddress is null)
        {
            return null;
        }

        // One lookup per TCP connection: the process at the other end cannot change while it stays open.
        var items = http.Features.Get<IConnectionItemsFeature>()?.Items;
        if (items is not null && items.TryGetValue(CallerKey, out var cached) && cached is CallerIdentity known)
        {
            return known;
        }

        try
        {
            var caller = _options.CallerResolver.Resolve(new IPEndPoint(c.RemoteIpAddress, c.RemotePort),
                new IPEndPoint(c.LocalIpAddress, c.LocalPort));
            if (caller is not null && items is not null)
            {
                items[CallerKey] = caller;
            }

            return caller;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null; // Fail closed: no identity means no access request.
        }
    }

    private static Dictionary<string, string> ReadQuery(HttpRequest request)
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in request.Query)
        {
            query[key] = value.ToString();
        }

        return query;
    }

    private static Dictionary<string, string> ReadHeaders(HttpRequest request)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in request.Headers)
        {
            headers[key] = value.ToString();
        }

        return headers;
    }

    private static async Task WriteErrorAsync(HttpContext http, string code, string message)
    {
        var envelope = ErrorCatalog.Create(code, message);
        http.Response.StatusCode = ErrorCatalog.Get(code).HttpStatus;
        http.Response.ContentType = "application/json; charset=utf-8";
        try
        {
            await http.Response.Body.WriteAsync(
                JsonSerializer.SerializeToUtf8Bytes(envelope, AiHostJsonContext.Default.ApiError),
                http.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
        }
    }

    private void WriteDiscovery()
    {
        if (_options.DiscoveryPath is not { } path)
        {
            return;
        }

        DiscoveryFileWriter.Write(path, new DiscoveryFile
        {
            BaseUrl = BaseUrl,
            Port = Port,
            Pid = Environment.ProcessId,
            InstanceId = _runtime.InstanceId,
            HostVersion = _runtime.HostVersion,
            StartedAt = StartedAt,
        });
    }

    /// <summary>Stops listening and removes the discovery file if it is still this host's own.</summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_app is { } app)
        {
            _app = null;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(ShutdownTimeout);
                await app.StopAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Past the deadline: the remaining connections are closed by DisposeAsync.
            }
            finally
            {
                await app.DisposeAsync().ConfigureAwait(false);
                if (_options.DiscoveryPath is { } path)
                {
                    DiscoveryFileWriter.DeleteIfOwn(path, _runtime.InstanceId);
                }
            }
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
