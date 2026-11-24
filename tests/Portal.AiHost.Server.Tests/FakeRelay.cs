using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Portal.AiHost.Core.Remote;

namespace Portal.AiHost.Server.Tests;

/// <summary>
/// A relay on a loopback port (never on the network), speaking the protocol of docs/remote-v1.md: host proof, stream
/// ids, tagged frames. It follows the same rules as the Worker, so the tests exercise the real wire format.
/// </summary>
internal sealed class FakeRelay : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<uint, WebSocket> _devices = new();
    private readonly SemaphoreSlim _hostSend = new(1, 1);
    private WebSocket? _host;
    private uint _next = 100;
    private int _hostConnections;
    private TaskCompletionSource _hostReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private FakeRelay(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    public int Port { get; }
    public Uri Url => new($"http://127.0.0.1:{Port}");

    /// <summary>The next host that connects is refused with this close code (host proof failed).</summary>
    public int? RefuseHostWith { get; set; }

    /// <summary>Host sockets that have connected (proven or not).</summary>
    public int HostConnections => Volatile.Read(ref _hostConnections);

    public bool HasHost => _host is { State: WebSocketState.Open };

    public int DeviceCount => _devices.Count;

    /// <summary>The room id the proven host belongs to.</summary>
    public string? Room { get; private set; }

    public static async Task<FakeRelay> StartAsync(int port = 0)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));
        var app = builder.Build();
        FakeRelay? relay = null;
        app.UseWebSockets();
        app.Run(ctx => relay!.HandleAsync(ctx));
        await app.StartAsync();
        var bound = new Uri(app.Urls.First()).Port;
        relay = new FakeRelay(app, bound);
        return relay;
    }

    public Task WaitHostAsync(TimeSpan? timeout = null) => _hostReady.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(10));

    private async Task HandleAsync(HttpContext ctx)
    {
        var parts = ctx.Request.Path.Value!.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts[0] != "v1" || !ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = 404;
            return;
        }

        if (parts[1] == "host")
        {
            await HostAsync(ctx, parts[2]);
        }
        else if (parts[1] == "device")
        {
            await DeviceAsync(ctx);
        }
        else
        {
            ctx.Response.StatusCode = 404;
        }
    }

    private async Task HostAsync(HttpContext ctx, string room)
    {
        Interlocked.Increment(ref _hostConnections);
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var nonce = RandomNumberGenerator.GetBytes(32);
        await SendTextAsync(ws, JsonSerializer.Serialize(new { t = "challenge", v = 1, room, nonce = RemoteCodec.Encode(nonce) }));
        var buffer = new byte[4 + 70000];
        var hello = await ReceiveAsync(ws, buffer);
        if (RefuseHostWith is { } code || !Proves(hello.Text, room, nonce))
        {
            await ws.CloseOutputAsync((WebSocketCloseStatus)(RefuseHostWith ?? 4401), "auth", CancellationToken.None);
            return;
        }

        var old = Interlocked.Exchange(ref _host, ws);
        if (old is { State: WebSocketState.Open })
        {
            try { await old.CloseOutputAsync((WebSocketCloseStatus)4409, "replaced", CancellationToken.None); } catch (WebSocketException) { }
        }

        foreach (var d in _devices.Values) _ = CloseQuietly(d, (WebSocketCloseStatus)4410);
        Room = room;
        await SendTextAsync(ws, """{"t":"ready","v":1}""");
        _hostReady.TrySetResult();
        try
        {
            while (true)
            {
                var m = await ReceiveAsync(ws, buffer);
                if (m.Closed) break;
                if (m.Text is { } text)
                {
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.TryGetProperty("t", out var t) && t.GetString() == "close"
                        && doc.RootElement.GetProperty("s").TryGetUInt32(out var s) && _devices.TryRemove(s, out var d))
                    {
                        await CloseQuietly(d, WebSocketCloseStatus.NormalClosure);
                    }
                }
                else if (m.Length >= 4 && _devices.TryGetValue(BinaryPrimitives.ReadUInt32BigEndian(buffer), out var device))
                {
                    await device.SendAsync(buffer.AsMemory(4, m.Length - 4), WebSocketMessageType.Binary, true, CancellationToken.None);
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
        finally
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _host, null, ws), ws))
            {
                foreach (var d in _devices.Values) _ = CloseQuietly(d, (WebSocketCloseStatus)4410);
                _hostReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    private static bool Proves(string? helloText, string room, byte[] nonce)
    {
        try
        {
            using var doc = JsonDocument.Parse(helloText ?? "");
            var root = doc.RootElement;
            if (root.GetProperty("t").GetString() != "hello"
                || !RemoteCodec.TryDecode(root.GetProperty("cert").GetString(), 2048, out var der)
                || !RemoteCodec.TryDecode(root.GetProperty("sig").GetString(), 64, out var sig))
            {
                return false;
            }

            if (RelayProtocol.RoomId(SHA256.HashData(der)) != room) return false;
            using var cert = X509CertificateLoader.LoadCertificate(der);
            using var key = cert.GetECDsaPublicKey() ?? throw new CryptographicException();
            return key.VerifyData(RelayProtocol.HostProofMessage(room, nonce), sig, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or CryptographicException or InvalidOperationException)
        {
            return false;
        }
    }

    private async Task DeviceAsync(HttpContext ctx)
    {
        var host = _host;
        if (host is not { State: WebSocketState.Open })
        {
            ctx.Response.StatusCode = 503;
            return;
        }

        if (_devices.Count >= 8)
        {
            ctx.Response.StatusCode = 429;
            return;
        }

        var id = Interlocked.Increment(ref _next);
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        _devices[id] = ws;
        await SendHostAsync(host, Encoding.UTF8.GetBytes($"{{\"t\":\"open\",\"s\":{id}}}"), WebSocketMessageType.Text);
        await SendTextAsync(ws, """{"t":"open"}""");
        var buffer = new byte[4 + 70000];
        try
        {
            while (true)
            {
                var m = await ReceiveAsync(ws, buffer.AsMemory(4));
                if (m.Closed) break;
                if (m.Text is not null) break;
                BinaryPrimitives.WriteUInt32BigEndian(buffer, id);
                await SendHostAsync(host, buffer.AsMemory(0, 4 + m.Length), WebSocketMessageType.Binary);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
        finally
        {
            if (_devices.TryRemove(id, out _))
            {
                try { await SendHostAsync(host, Encoding.UTF8.GetBytes($"{{\"t\":\"close\",\"s\":{id}}}"), WebSocketMessageType.Text); }
                catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException) { }
            }
        }
    }

    private async Task SendHostAsync(WebSocket host, ReadOnlyMemory<byte> data, WebSocketMessageType type)
    {
        await _hostSend.WaitAsync();
        try { await host.SendAsync(data, type, true, CancellationToken.None); }
        finally { _hostSend.Release(); }
    }

    private static Task SendTextAsync(WebSocket ws, string text) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task CloseQuietly(WebSocket ws, WebSocketCloseStatus status)
    {
        try { await ws.CloseOutputAsync(status, "closed", CancellationToken.None); }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException) { }
    }

    private readonly record struct Received(string? Text, int Length, bool Closed);

    private static Task<Received> ReceiveAsync(WebSocket ws, byte[] buffer) => ReceiveAsync(ws, buffer.AsMemory());

    private static async Task<Received> ReceiveAsync(WebSocket ws, Memory<byte> buffer)
    {
        var used = 0;
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer[used..], CancellationToken.None);
            if (r.MessageType == WebSocketMessageType.Close) return new Received(null, 0, true);
            used += r.Count;
            if (r.EndOfMessage)
            {
                return new Received(r.MessageType == WebSocketMessageType.Text ? Encoding.UTF8.GetString(buffer.Span[..used]) : null, used, false);
            }
        }
    }

    /// <summary>Stops the relay as if the network dropped.</summary>
    public async ValueTask DisposeAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try { await _app.StopAsync(cts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
        await _app.DisposeAsync();
    }
}

/// <summary>A device's end of a relay stream, usable as the transport under TLS.</summary>
internal sealed class WebSocketStream : Stream
{
    private readonly ClientWebSocket _ws;
    private readonly byte[] _frame = new byte[70000];
    private int _have;
    private int _read;
    private bool _eof;

    public WebSocketStream(ClientWebSocket ws) => _ws = ws;

    public static async Task<WebSocketStream> ConnectAsync(Uri relay, string room, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        var uri = new UriBuilder(relay) { Scheme = relay.Scheme == "https" ? "wss" : "ws", Path = $"/v1/device/{room}" }.Uri;
        await ws.ConnectAsync(uri, ct);
        var s = new WebSocketStream(ws);
        // The relay's first message is the text {"t":"open"}; skip it.
        var r = await ws.ReceiveAsync(s._frame, ct);
        if (r.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("expected open");
        return s;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        while (_read == _have)
        {
            if (_eof) return 0;
            var r = await _ws.ReceiveAsync(_frame, ct);
            if (r.MessageType == WebSocketMessageType.Close) { _eof = true; return 0; }
            _have = r.Count;
            _read = 0;
        }

        var n = Math.Min(buffer.Length, _have - _read);
        _frame.AsMemory(_read, n).CopyTo(buffer);
        _read += n;
        return n;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        for (var o = 0; o < buffer.Length; o += 16 * 1024)
        {
            await _ws.SendAsync(buffer.Slice(o, Math.Min(16 * 1024, buffer.Length - o)), WebSocketMessageType.Binary, true, ct);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _ws.Dispose();
        base.Dispose(disposing);
    }
}
