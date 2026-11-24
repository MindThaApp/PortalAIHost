using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Remote;
using Portal.AiHost.Core.Runtime;

namespace Portal.AiHost.Server;

public sealed class LanServerOptions
{
    public required X509Certificate2 Certificate { get; init; }

    /// <summary>TCP port; 0 lets the system choose (tests).</summary>
    public int Port { get; init; } = RemoteApi.DefaultPort;

    /// <summary>Address to listen on; null listens on every network interface (tests use loopback).</summary>
    public IPAddress? BindAddress { get; init; }

    /// <summary>Which source addresses are served. Default: private and link-local networks only.</summary>
    public Func<IPAddress?, bool> AllowSource { get; init; } = RemoteNetwork.IsPrivateAddress;

    public int MaxRequestBytes { get; init; } = ApiLimits.MaxRequestBytes;

    /// <summary>
    /// Also listen on an internal loopback-only endpoint (random port) for the relay bridge. Connections that arrive
    /// there are accepted although they come from this PC; the device still has to pair and sign every request.
    /// </summary>
    public bool EnableBridgeEndpoint { get; init; }
}

/// <summary>
/// The LAN listener: HTTPS with the host's self-signed certificate, connections only from private or link-local
/// addresses (refused before the TLS handshake). The pairing steps are answered here; every other request must carry the
/// signature of a paired device (docs/remote-v1.md) and then goes to the same <see cref="AiHostRuntime.Pipeline"/> as
/// the loopback listener, with the device as its caller. There are no admin routes.
/// </summary>
public sealed class LanServer : IAsyncDisposable
{
    private const int MaxPairingBodyBytes = 8 * 1024;

    private readonly AiHostRuntime _runtime;
    private readonly PairingService _pairing;
    private readonly DeviceRequestVerifier _verifier;
    private readonly DeviceSessions _sessions;
    private readonly LanServerOptions _options;
    private WebApplication? _app;

    /// <summary>Open TCP connections allowed at once.</summary>
    public const int MaxConnections = 64;

    private LanServer(AiHostRuntime runtime, PairingService pairing, DeviceRequestVerifier verifier,
        DeviceSessions sessions, LanServerOptions options)
    {
        _sessions = sessions;
        _runtime = runtime;
        _pairing = pairing;
        _verifier = verifier;
        _options = options;
    }

    public int Port { get; private set; }

    /// <summary>The internal loopback endpoint of the relay bridge; null unless <see cref="LanServerOptions.EnableBridgeEndpoint"/>.</summary>
    public IPEndPoint? BridgeEndPoint { get; private set; }

    private ListenOptions? _bridgeListen;

    private const string BridgeItem = "portal.bridge";

    public static async Task<LanServer> StartAsync(AiHostRuntime runtime, PairingService pairing,
        DeviceRequestVerifier verifier, DeviceSessions sessions, LanServerOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(pairing);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(options);
        var server = new LanServer(runtime, pairing, verifier, sessions, options);
        var app = server.Build();
        try
        {
            await app.StartAsync(ct).ConfigureAwait(false);
            server._app = app;
            server.Port = options.Port != 0 ? options.Port : BoundPort(app);
            if (server._bridgeListen is { } bridge)
            {
                server.BridgeEndPoint = bridge.IPEndPoint is { Port: > 0 } ep
                    ? ep
                    : throw new InvalidOperationException("The bridge endpoint has no bound port.");
            }

            return server;
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static int BoundPort(WebApplication app)
    {
        foreach (var a in app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? [])
        {
            if (Uri.TryCreate(a, UriKind.Absolute, out var uri) && uri.Port > 0)
            {
                return uri.Port;
            }
        }

        throw new InvalidOperationException("The listener has no bound port.");
    }

    private WebApplication Build()
    {
        // Same empty builder as the loopback listener: nothing outside this code can add or change a listener.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            Args = [],
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.WebHost.UseKestrelCore();
        builder.Services.Configure<Microsoft.Extensions.Hosting.HostOptions>(o => o.ShutdownTimeout = AiHostServer.ShutdownTimeout);
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxConcurrentConnections = MaxConnections;
            k.Limits.MaxRequestBodySize = (long)_options.MaxRequestBytes + 1;
            k.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            void Configure(ListenOptions listen)
            {
                listen.Protocols = HttpProtocols.Http1;

                // Before the TLS handshake: a source outside the home network gets nothing, not even a handshake.
                listen.Use(next => context =>
                {
                    if (!_options.AllowSource((context.RemoteEndPoint as IPEndPoint)?.Address))
                    {
                        context.Abort();
                        return Task.CompletedTask;
                    }

                    return next(context);
                });
                listen.UseHttps(_options.Certificate);
            }

            if (_options.BindAddress is { } address)
            {
                k.Listen(address, _options.Port, Configure);
            }
            else
            {
                k.ListenAnyIP(_options.Port, Configure);
            }

            if (_options.EnableBridgeEndpoint)
            {
                // Loopback only, so nothing on the network can reach it; the TLS identity and the device signatures
                // are the same as on the LAN endpoint. Marked here so the source check below knows the origin.
                k.Listen(IPAddress.Loopback, 0, listen =>
                {
                    _bridgeListen = listen;
                    listen.Protocols = HttpProtocols.Http1;
                    listen.Use(next => context =>
                    {
                        context.Items[BridgeItem] = true;
                        return next(context);
                    });
                    listen.UseHttps(_options.Certificate);
                });
            }
        });
        var app = builder.Build();
        app.Run(HandleAsync);
        return app;
    }

    private async Task HandleAsync(HttpContext http)
    {
        var request = http.Request;
        var viaBridge = http.Features.Get<IConnectionItemsFeature>()?.Items.ContainsKey(BridgeItem) == true;
        if (!viaBridge && !_options.AllowSource(http.Connection.RemoteIpAddress))
        {
            http.Abort();
            return;
        }

        if (RequestGuard.IsBrowserRequest(request))
        {
            await AiHostServer.WriteErrorAsync(http, ErrorCodes.ForbiddenOrigin, "Browser requests are not accepted.")
                .ConfigureAwait(false);
            return;
        }

        var path = request.Path.HasValue ? request.Path.Value! : "/";
        var isPairing = path is RemoteApi.PairStartPath or RemoteApi.PairFinishPath;
        var limit = isPairing ? MaxPairingBodyBytes : _options.MaxRequestBytes;

        RequestGuard.BodyResult result;
        byte[] body;
        try
        {
            (result, body) = await RequestGuard.ReadBodyAsync(request, limit, http.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (http.RequestAborted.IsCancellationRequested &&
            ex is IOException or OperationCanceledException or Microsoft.AspNetCore.Http.BadHttpRequestException)
        {
            return;
        }
        catch (Exception ex) when (ex is Microsoft.AspNetCore.Http.BadHttpRequestException or IOException)
        {
            http.Response.Headers.Connection = "close";
            await AiHostServer.WriteErrorAsync(http, ErrorCodes.InvalidRequest, "The request body could not be read.")
                .ConfigureAwait(false);
            return;
        }

        if (result == RequestGuard.BodyResult.TooLarge)
        {
            http.Response.Headers.Connection = "close";
            await AiHostServer.WriteErrorAsync(http, ErrorCodes.RequestTooLarge,
                $"The request body is larger than {limit} bytes.").ConfigureAwait(false);
            return;
        }

        if (isPairing)
        {
            await HandlePairingAsync(http, path, body).ConfigureAwait(false);
            return;
        }

        var headers = AiHostServer.ReadHeaders(request);

        // The request target as it was sent (not decoded), because that is what the device signed.
        var target = http.Features.Get<IHttpRequestFeature>()?.RawTarget ?? path + request.QueryString.Value;
        var auth = _verifier.Verify(request.Method, target, headers, body);
        if (auth.Device is null && auth.FailureReason == DeviceAuthReasons.RateLimited)
        {
            await AiHostServer.WriteErrorAsync(http, ErrorCodes.RateLimited, "This device is sending too many requests.",
                Reason(auth.FailureReason), auth.RetryAfterMs).ConfigureAwait(false);
            return;
        }

        if (auth.Device is not { } device)
        {
            await AiHostServer.WriteErrorAsync(http, ErrorCodes.DeviceUnauthorized,
                "This device is not paired with this PC, or the request signature is not valid.",
                Reason(auth.FailureReason)).ConfigureAwait(false);
            return;
        }

        // Admitted only while the device is still paired. Removing the device cancels this token: it is linked into
        // the request (so queued and running generations stop) and it closes the connection (so a stream ends).
        if (!_sessions.TryEnter(device.Id, out var revoked))
        {
            await AiHostServer.WriteErrorAsync(http, ErrorCodes.DeviceUnauthorized,
                "This device is not paired with this PC, or the request signature is not valid.",
                Reason(DeviceAuthReasons.UnknownDevice)).ConfigureAwait(false);
            return;
        }

        using var link = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, revoked);
        using var abort = revoked.Register(static state => ((HttpContext)state!).Abort(), http);

        var apiRequest = new ApiRequest
        {
            Method = request.Method,
            Path = path,
            Query = AiHostServer.ReadQuery(request),
            Headers = headers,
            Body = body,
            Caller = new CallerIdentity(0, null, null, Verified: false, SameUser: true,
                Remote: new RemoteDevice(device.Id, device.Name)),
        };
        await _runtime.Pipeline.HandleAsync(apiRequest, new KestrelResponseSink(http, link.Token)).ConfigureAwait(false);
    }

    private async Task HandlePairingAsync(HttpContext http, string path, byte[] body)
    {
        if (!string.Equals(http.Request.Method, "POST", StringComparison.Ordinal))
        {
            await AiHostServer.WriteErrorAsync(http, ErrorCodes.NotFound, "Unknown route.").ConfigureAwait(false);
            return;
        }

        if (!Core.Api.RequestValidator.IsJsonContentType(http.Request.ContentType))
        {
            await AiHostServer.WriteErrorAsync(http, ErrorCodes.InvalidRequest, "Content-Type must be application/json.")
                .ConfigureAwait(false);
            return;
        }

        try
        {
            byte[] answer;
            if (path == RemoteApi.PairStartPath)
            {
                var start = JsonSerializer.Deserialize(body, AiHostJsonContext.Default.PairStartRequest);
                answer = JsonSerializer.SerializeToUtf8Bytes(
                    _pairing.Start(start ?? throw new JsonException()), AiHostJsonContext.Default.PairStartResponse);
            }
            else
            {
                var finish = JsonSerializer.Deserialize(body, AiHostJsonContext.Default.PairFinishRequest);
                answer = JsonSerializer.SerializeToUtf8Bytes(
                    _pairing.Finish(finish ?? throw new JsonException()), AiHostJsonContext.Default.PairFinishResponse);
            }

            http.Response.StatusCode = StatusCodes.Status200OK;
            http.Response.ContentType = "application/json; charset=utf-8";
            await http.Response.Body.WriteAsync(answer, http.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await AiHostServer.WriteErrorAsync(http, ErrorCodes.InvalidRequest, "The body is not valid JSON for this step.")
                .ConfigureAwait(false);
        }
        catch (PairingException ex)
        {
            await AiHostServer.WriteErrorAsync(http, ErrorCodes.PairingFailed, "Pairing was refused.", Reason(ex.Reason))
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await AiHostServer.WriteErrorAsync(http, ErrorCodes.InternalError, "Pairing could not be completed.")
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
        }
    }

    private static Dictionary<string, JsonElement> Reason(string? reason)
    {
        // The reasons are fixed identifiers (no quotes or escapes), never request text.
        using var doc = JsonDocument.Parse($"\"{reason}\"");
        return new Dictionary<string, JsonElement> { ["reason"] = doc.RootElement.Clone() };
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_app is { } app)
        {
            _app = null;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(AiHostServer.ShutdownTimeout);
                await app.StopAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                await app.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
