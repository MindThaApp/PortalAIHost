using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;

namespace Portal.AiHost.Core.Remote.Client;

/// <summary>
/// The relay turned the connection down, or ended it in a way that says why. These are the relay's words, never the PC's:
/// they can make the PC "unreachable" but never "revoked" or "certificate changed" (docs/client-mode-v1.md section 3.1).
/// </summary>
public sealed class RelayException(string reason, int? retryAfterMs, string message) : IOException(message)
{
    /// <summary>A <see cref="RemoteLinkReasons"/> value: <c>relay_host_offline</c>, <c>relay_busy</c> or <c>offline</c>.</summary>
    public string Reason { get; } = reason;

    public int? RetryAfterMs { get; } = retryAfterMs;

    /// <summary>The wait that follows a budget refusal (HTTP 429 or close 4429): one minute (docs/remote-v1.md section 10.4).</summary>
    public const int BudgetRetryMs = 60_000;

    /// <summary>Maps the relay's HTTP status before the upgrade (10.2 step 1). Null when it says nothing about the relay's state.</summary>
    public static RelayException? FromHttpStatus(int status) => status switch
    {
        503 => new RelayException(RemoteLinkReasons.RelayHostOffline, null, "The relay says the PC is not connected (host_offline)."),
        429 => new RelayException(RemoteLinkReasons.RelayBusy, BudgetRetryMs, "The relay refused another stream (429)."),
        _ => null,
    };

    /// <summary>Maps a close code of the stream (10.2 table). Null for a plain end of the stream (1000, 4408, others).</summary>
    public static RelayException? FromCloseCode(int code) => code switch
    {
        4404 or 4410 => new RelayException(RemoteLinkReasons.RelayHostOffline, null, $"The PC left the relay ({code})."),
        4429 => new RelayException(RemoteLinkReasons.RelayBusy, BudgetRetryMs, "The relay budget of this PC is used up (4429)."),
        4403 => new RelayException(RemoteLinkReasons.RelayAdmission, null, "The relay wants an access key this device does not have (4403)."),
        _ => null,
    };
}

/// <summary>
/// One relay stream as a <see cref="Stream"/> (docs/remote-v1.md section 10.2): reads return the payload of binary
/// messages, writes become binary messages of at most 65536 bytes. The only text this side sends is <c>ping</c>, about
/// every 45 seconds while nothing else moves; <c>pong</c> and any other text from the relay is skipped.
/// </summary>
public sealed class RelayWebSocketStream : Stream
{
    /// <summary>Largest payload of one binary message (the relay closes the stream with 1009 above it).</summary>
    public const int MaxFrameBytes = 65536;

    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(45);

    private static readonly byte[] PingText = "ping"u8.ToArray();

    private readonly WebSocket _ws;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly byte[] _frame = new byte[MaxFrameBytes];
    private readonly ITimer? _pinger;
    private int _have;
    private int _read;
    private IOException? _closed;
    private bool _eof;
    private long _activity;

    public RelayWebSocketStream(WebSocket ws, TimeProvider? time = null)
    {
        _ws = ws ?? throw new ArgumentNullException(nameof(ws));
        _pinger = (time ?? TimeProvider.System).CreateTimer(_ => _ = PingAsync(), null, PingInterval, PingInterval);
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0)
        {
            return 0;
        }

        while (_read == _have)
        {
            if (_closed is not null)
            {
                throw _closed;
            }

            if (_eof)
            {
                return 0;
            }

            ValueWebSocketReceiveResult r;
            try
            {
                r = await _ws.ReceiveAsync(_frame.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            catch (WebSocketException ex)
            {
                throw new IOException("The relay connection ended.", ex);
            }

            if (r.MessageType == WebSocketMessageType.Close)
            {
                _closed = RelayException.FromCloseCode((int?)_ws.CloseStatus ?? 0);
                _eof = true;
                if (_closed is not null)
                {
                    throw _closed;
                }

                return 0;
            }

            if (r.MessageType == WebSocketMessageType.Text)
            {
                continue; // "pong" (or any note from the relay): not data, and not activity.
            }

            Interlocked.Exchange(ref _activity, Environment.TickCount64);
            _have = r.Count;
            _read = 0;
        }

        var n = Math.Min(buffer.Length, _have - _read);
        _frame.AsMemory(_read, n).CopyTo(buffer);
        _read += n;
        return n;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var o = 0; o < buffer.Length; o += MaxFrameBytes)
            {
                var piece = buffer.Slice(o, Math.Min(MaxFrameBytes, buffer.Length - o));
                await _ws.SendAsync(piece, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
            }

            Interlocked.Exchange(ref _activity, Environment.TickCount64);
        }
        catch (WebSocketException ex)
        {
            throw new IOException("The relay connection ended.", ex);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task PingAsync()
    {
        // Only on an idle stream: data in the last interval is its own proof of life.
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _activity) < PingInterval.TotalMilliseconds / 2 || _ws.State != WebSocketState.Open)
        {
            return;
        }

        try
        {
            await _sendGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await _ws.SendAsync(PingText, WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _sendGate.Release();
            }
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            // The read side reports the end of the stream.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pinger?.Dispose();
            _ws.Abort();
            _ws.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// Reaches the PC through the relay (docs/client-mode-v1.md section 3.1): opens the device WebSocket of the room derived
/// from the pinned fingerprint, waits for <c>{"t":"open"}</c>, then runs TLS over the stream with the same full-fingerprint
/// pin as the LAN. The handshake happens inside the connect step, so one timeout (5 s) covers WebSocket, open message and
/// TLS, and nothing of the request leaves the laptop unless the pin matched. HTTP/1.1 runs on top through the same
/// channel code as the LAN.
/// </summary>
public sealed class RelayConnector : IRemoteConnector
{
    /// <summary>Connect timeout of the relay route (docs/client-mode-v1.md section 4.6).</summary>
    public static readonly TimeSpan LinkConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The relay allows 3 streams per address; the channel never opens more at once, further requests wait.</summary>
    public const int MaxStreams = 3;

    private readonly Uri _relay;
    private readonly string _room;
    private readonly TimeSpan _connectTimeout;
    private readonly string? _admission;

    /// <param name="admission">The PC's relay admission token from the pairing link (32 bytes), sent in a request header; empty when the PC asked for none.</param>
    public RelayConnector(Uri relayUrl, ReadOnlySpan<byte> fingerprint, TimeSpan? connectTimeout = null, ReadOnlySpan<byte> admission = default)
    {
        _admission = admission.Length == RelayProtocol.AdmissionTokenBytes ? RemoteCodec.Encode(admission) : null;
        _relay = relayUrl ?? throw new ArgumentNullException(nameof(relayUrl));
        _room = RelayProtocol.RoomId(fingerprint);
        _connectTimeout = connectTimeout ?? LinkConnectTimeout;
    }

    public IRemoteChannel Open(string address, int port, Func<byte[], bool> acceptCertificate)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = _connectTimeout,
            MaxConnectionsPerServer = MaxStreams,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            // The stream already carries TLS (set up below), so HTTP runs on it as plain "http" to the handler.
            ConnectCallback = (context, ct) => ConnectAsync(address, acceptCertificate, ct),
        };
        var host = address.Contains(':') ? $"[{address}]" : address;
        return new LanConnector.Channel(new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri($"http://{host}:{port}"),
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        });
    }

    private async ValueTask<Stream> ConnectAsync(string address, Func<byte[], bool> acceptCertificate, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(_connectTimeout);
        var token = limit.Token;
        var ws = new ClientWebSocket();
        ws.Options.CollectHttpResponseDetails = true;
        if (_admission is not null)
        {
            // A header, never the URL: query strings end up in logs.
            ws.Options.SetRequestHeader(RelayProtocol.AdmissionHeader, _admission);
        }

        RelayWebSocketStream? stream = null;
        try
        {
            var uri = new UriBuilder(_relay)
            {
                Scheme = _relay.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
                Path = $"/v1/device/{_room}",
            }.Uri;
            try
            {
                await ws.ConnectAsync(uri, token).ConfigureAwait(false);
            }
            catch (WebSocketException ex)
            {
                throw (ws.HttpStatusCode is { } status ? RelayException.FromHttpStatus((int)status) : null)
                    ?? new IOException("The relay could not be reached.", ex);
            }

            await WaitForOpenAsync(ws, token).ConfigureAwait(false);
            stream = new RelayWebSocketStream(ws);
            var tls = new SslStream(stream, leaveInnerStreamOpen: false);
            try
            {
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = address,
                    RemoteCertificateValidationCallback = (_, cert, _, _) =>
                        cert is not null && acceptCertificate(cert.GetRawCertData()),
                }, token).ConfigureAwait(false);
            }
            catch
            {
                await tls.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            return tls;
        }
        catch
        {
            if (stream is null)
            {
                ws.Dispose();
            }

            throw;
        }
    }

    /// <summary>The relay's first message is the text <c>{"t":"open"}</c> (10.2 step 2); a close instead says why not.</summary>
    private static async Task WaitForOpenAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[256];
        var used = 0;
        try
        {
            while (true)
            {
                var r = await ws.ReceiveAsync(buffer.AsMemory(used), ct).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    throw RelayException.FromCloseCode((int?)ws.CloseStatus ?? 0)
                        ?? new IOException("The relay closed the stream before it opened.");
                }

                if (r.MessageType != WebSocketMessageType.Text)
                {
                    throw new InvalidDataException("The relay sent data before the stream opened.");
                }

                used += r.Count;
                if (r.EndOfMessage)
                {
                    break;
                }

                if (used == buffer.Length)
                {
                    throw new InvalidDataException("The relay's first message is too long.");
                }
            }
        }
        catch (WebSocketException ex)
        {
            throw new IOException("The relay connection ended.", ex);
        }

        try
        {
            using var doc = JsonDocument.Parse(buffer.AsMemory(0, used));
            if (doc.RootElement.TryGetProperty("t", out var t) && t.GetString() == "open")
            {
                return;
            }
        }
        catch (JsonException)
        {
        }

        throw new InvalidDataException("The relay's first message was not open: " + Encoding.UTF8.GetString(buffer, 0, Math.Min(used, 40)));
    }
}
