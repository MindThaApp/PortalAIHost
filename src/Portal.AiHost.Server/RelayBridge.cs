using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Remote;

namespace Portal.AiHost.Server;

public sealed class RelayBridgeOptions
{
    /// <summary>https (or http, for a relay on this PC in tests); the host adds /v1/host/&lt;room&gt;.</summary>
    public required Uri Url { get; init; }

    /// <summary>The host's certificate with its private key: the relay checks that the host owns it.</summary>
    public required X509Certificate2 Certificate { get; init; }

    /// <summary>The internal loopback listener each stream is piped to (<see cref="LanServer.BridgeEndPoint"/>).</summary>
    public required IPEndPoint LocalTarget { get; init; }

    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>A link must stay up this long before the reconnect delay starts over (stops a flapping link from hammering the relay).</summary>
    public TimeSpan StableAfter { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>A text "ping" is sent this often; a link that stays silent for 2.5 times as long is dropped.</summary>
    public TimeSpan KeepAlive { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>Streams open at once (the relay allows 8 per room; this is a safety stop).</summary>
    public int MaxStreams { get; init; } = 16;
}

/// <summary>
/// Keeps one outbound WebSocket to the relay while other devices are allowed away from home. Each stream the relay
/// opens becomes one TCP connection to the internal loopback listener of <see cref="LanServer"/>, and the bytes are
/// copied both ways. The bytes are the device's TLS session with this PC, so the bridge never sees plaintext and does
/// no authentication of devices: that stays inside the LAN listener (pairing, signed requests).
/// </summary>
public sealed class RelayBridge : IAsyncDisposable
{
    private const int MaxFramePayload = 64 * 1024;
    private const int ReadChunk = 16 * 1024;

    private readonly RelayBridgeOptions _o;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly ConcurrentDictionary<uint, StreamPipe> _streams = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Lock _stateGate = new();
    private string _state = RelayStates.Connecting;
    private int _disposed;

    private RelayBridge(RelayBridgeOptions options)
    {
        _o = options;
        RoomId = RelayProtocol.RoomId(HostCertificate.Fingerprint(options.Certificate));
        _loop = Task.Run(() => RunAsync(_stop.Token));
    }

    public static RelayBridge Start(RelayBridgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new RelayBridge(options);
    }

    public string RoomId { get; }

    /// <summary>One of <see cref="RelayStates"/>.</summary>
    public string State
    {
        get { lock (_stateGate) { return _state; } }
    }

    public event Action? StateChanged;

    /// <summary>Streams currently open.</summary>
    public int StreamCount => _streams.Count;

    private void SetState(string state)
    {
        lock (_stateGate)
        {
            if (_state == state) return;
            _state = state;
        }

        StateChanged?.Invoke();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var delay = _o.InitialDelay;
        while (!ct.IsCancellationRequested)
        {
            var connected = false;
            long upSince = 0;
            var wait = delay;
            try
            {
                await SessionAsync(() => { connected = true; upSince = Environment.TickCount64; }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (RelayRefusedException)
            {
                // Replaced by another copy of this host, or refused: do not fight over the room.
                wait = _o.MaxDelay;
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or HttpRequestException or SocketException
                or InvalidDataException or JsonException or OperationCanceledException or TimeoutException or ObjectDisposedException)
            {
                // The relay is not reachable or the link broke: retried below.
            }

            if (connected && TimeSpan.FromMilliseconds(Environment.TickCount64 - upSince) >= _o.StableAfter)
            {
                delay = _o.InitialDelay; // it stayed up: start over quickly
                wait = delay;
                SetState(RelayStates.Connecting);
            }
            else if (connected)
            {
                // Came up but dropped quickly: keep backing off.
                delay = TimeSpan.FromTicks(Math.Min(_o.MaxDelay.Ticks, delay.Ticks * 2));
                SetState(RelayStates.Connecting);
            }
            else
            {
                SetState(RelayStates.Unreachable);
                delay = TimeSpan.FromTicks(Math.Min(_o.MaxDelay.Ticks, delay.Ticks * 2));
            }

            if (wait < _o.MaxDelay)
            {
                wait = wait * (0.8 + Random.Shared.NextDouble() * 0.4);
            }

            try
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private Uri HostUri()
    {
        var b = new UriBuilder(_o.Url)
        {
            Scheme = _o.Url.Scheme == Uri.UriSchemeHttp ? "ws" : "wss",
            Path = $"/v1/host/{RoomId}",
            Query = "",
        };
        if (_o.Url.IsDefaultPort) b.Port = -1;
        return b.Uri;
    }

    private async Task SessionAsync(Action onReady, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        using var link = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connect.CancelAfter(_o.ConnectTimeout);
                try
                {
                    await ws.ConnectAsync(HostUri(), connect.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException("The relay did not answer.");
                }
            }

            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshake.CancelAfter(_o.HandshakeTimeout);
            var buffer = new byte[MaxFramePayload + 4 + 16];
            try
            {
                await HandshakeAsync(ws, buffer, handshake.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("The relay did not accept this host in time.");
            }

            SetState(RelayStates.Connected);
            onReady();
            var lastHeard = Environment.TickCount64;
            var keep = KeepAliveAsync(ws, () => Interlocked.Read(ref lastHeard), link.Token);
            try
            {
                await ReceiveLoopAsync(ws, buffer, () => Interlocked.Exchange(ref lastHeard, Environment.TickCount64), link.Token)
                    .ConfigureAwait(false);
            }
            finally
            {
                await link.CancelAsync().ConfigureAwait(false);
                try { await keep.ConfigureAwait(false); } catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
            }
        }
        finally
        {
            foreach (var s in _streams.Values) s.Dispose();
            _streams.Clear();
            if (ws.State == WebSocketState.Open)
            {
                // Say goodbye so the relay frees the room at once instead of waiting for the connection to time out.
                try
                {
                    using var bye = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", bye.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
                {
                }
            }
        }
    }

    private async Task HandshakeAsync(ClientWebSocket ws, byte[] buffer, CancellationToken ct)
    {
        var first = await ReceiveMessageAsync(ws, buffer, ct).ConfigureAwait(false);
        using var challenge = JsonDocument.Parse(first.Text ?? throw new InvalidDataException("No challenge."));
        var root = challenge.RootElement;
        if (root.GetProperty("t").GetString() != "challenge" || root.GetProperty("room").GetString() != RoomId)
        {
            throw new InvalidDataException("Unexpected challenge.");
        }

        if (!RemoteCodec.TryDecode(root.GetProperty("nonce").GetString(), 64, out var nonce))
        {
            throw new InvalidDataException("Bad nonce.");
        }

        byte[] sig;
        using (var key = _o.Certificate.GetECDsaPrivateKey() ?? throw new InvalidOperationException("The certificate has no ECDSA key."))
        {
            sig = RelayProtocol.SignHostProof(key, RoomId, nonce);
        }

        var hello = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
        {
            ["t"] = "hello",
            ["cert"] = RemoteCodec.Encode(_o.Certificate.RawData),
            ["sig"] = RemoteCodec.Encode(sig),
        }, RelayJson.Default.DictionaryStringString);
        await SendAsync(ws, hello, WebSocketMessageType.Text, ct).ConfigureAwait(false);

        var answer = await ReceiveMessageAsync(ws, buffer, ct).ConfigureAwait(false);
        using var ready = JsonDocument.Parse(answer.Text ?? throw new InvalidDataException("No answer."));
        if (ready.RootElement.GetProperty("t").GetString() != "ready")
        {
            throw new InvalidDataException("The relay did not accept this host.");
        }
    }

    private readonly record struct Message(string? Text, int Length);

    /// <summary>Reads one whole message into <paramref name="buffer"/>. A close frame raises <see cref="RelayRefusedException"/> or ends the session.</summary>
    private static async Task<Message> ReceiveMessageAsync(ClientWebSocket ws, byte[] buffer, CancellationToken ct)
    {
        var used = 0;
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer.AsMemory(used), ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close)
            {
                var code = (int?)ws.CloseStatus ?? 0;
                throw code is 4401 or 4409
                    ? new RelayRefusedException(code)
                    : new WebSocketException(WebSocketError.ConnectionClosedPrematurely, "The relay closed the connection.");
            }

            used += r.Count;
            if (r.EndOfMessage)
            {
                return new Message(r.MessageType == WebSocketMessageType.Text ? Encoding.UTF8.GetString(buffer, 0, used) : null, used);
            }

            if (used >= buffer.Length)
            {
                throw new InvalidDataException("A relay message is too large.");
            }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, byte[] buffer, Action heard, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var m = await ReceiveMessageAsync(ws, buffer, ct).ConfigureAwait(false);
            heard();
            if (m.Text is { } text)
            {
                HandleControl(ws, text, ct);
                continue;
            }

            if (m.Length < 4) continue;
            var id = BinaryPrimitives.ReadUInt32BigEndian(buffer);
            if (_streams.TryGetValue(id, out var pipe) && m.Length > 4)
            {
                var payload = buffer.AsSpan(4, m.Length - 4).ToArray();
                if (!pipe.TryEnqueue(payload))
                {
                    CloseStream(id, tellRelay: ws, ct);
                }
            }
        }
    }

    private void HandleControl(ClientWebSocket ws, string text, CancellationToken ct)
    {
        if (text == "pong") return;
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (!root.TryGetProperty("t", out var t) || !root.TryGetProperty("s", out var sProp) || !sProp.TryGetUInt32(out var id))
        {
            return;
        }

        switch (t.GetString())
        {
            case "open":
                if (_streams.ContainsKey(id)) return;
                if (_streams.Count >= _o.MaxStreams)
                {
                    _ = SendCloseAsync(ws, id, ct);
                    return;
                }

                var pipe = new StreamPipe(id);
                if (_streams.TryAdd(id, pipe))
                {
                    _ = Task.Run(() => PumpAsync(ws, pipe, ct), CancellationToken.None);
                }

                break;
            case "close":
                CloseStream(id, tellRelay: null, ct);
                break;
        }
    }

    private void CloseStream(uint id, ClientWebSocket? tellRelay, CancellationToken ct)
    {
        if (_streams.TryRemove(id, out var pipe))
        {
            pipe.Dispose();
        }

        if (tellRelay is not null) _ = SendCloseAsync(tellRelay, id, ct);
    }

    private async Task SendCloseAsync(ClientWebSocket ws, uint id, CancellationToken ct)
    {
        try
        {
            var msg = Encoding.UTF8.GetBytes($"{{\"t\":\"close\",\"s\":{id}}}");
            await SendAsync(ws, msg, WebSocketMessageType.Text, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            // The link is going down anyway.
        }
    }

    /// <summary>One stream: connect to the internal listener, then copy bytes both ways until either side ends.</summary>
    private async Task PumpAsync(ClientWebSocket ws, StreamPipe pipe, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient { NoDelay = true };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, pipe.Token);
            await tcp.ConnectAsync(_o.LocalTarget, cts.Token).ConfigureAwait(false);
            await using var net = tcp.GetStream();
            var down = Task.Run(async () =>
            {
                await foreach (var chunk in pipe.Reader.ReadAllAsync(cts.Token).ConfigureAwait(false))
                {
                    await net.WriteAsync(chunk, cts.Token).ConfigureAwait(false);
                }
            }, CancellationToken.None);
            var up = Task.Run(async () =>
            {
                var frame = new byte[4 + ReadChunk];
                BinaryPrimitives.WriteUInt32BigEndian(frame, pipe.Id);
                while (true)
                {
                    var n = await net.ReadAsync(frame.AsMemory(4), cts.Token).ConfigureAwait(false);
                    if (n == 0) return;
                    await SendAsync(ws, frame.AsMemory(0, 4 + n), WebSocketMessageType.Binary, cts.Token).ConfigureAwait(false);
                }
            }, CancellationToken.None);
            await Task.WhenAny(down, up).ConfigureAwait(false);
            await cts.CancelAsync().ConfigureAwait(false);
            try { await Task.WhenAll(down, up).ConfigureAwait(false); } catch (Exception ex) when (IsExpected(ex)) { }
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            // The local side ended, could not be reached, or the stream was closed by the relay.
        }
        finally
        {
            // Closed by the relay already: nothing to tell. Ended here: tell the relay.
            if (((ICollection<KeyValuePair<uint, StreamPipe>>)_streams).Remove(new KeyValuePair<uint, StreamPipe>(pipe.Id, pipe)))
            {
                pipe.Dispose();
                await SendCloseAsync(ws, pipe.Id, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static bool IsExpected(Exception ex) =>
        ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or WebSocketException
            or InvalidOperationException or ChannelClosedException;

    private async Task KeepAliveAsync(ClientWebSocket ws, Func<long> lastHeard, CancellationToken ct)
    {
        var ping = "ping"u8.ToArray();
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_o.KeepAlive, ct).ConfigureAwait(false);
            if (Environment.TickCount64 - lastHeard() > _o.KeepAlive.TotalMilliseconds * 2.5)
            {
                ws.Abort(); // silent for too long: the link is dead
                return;
            }

            await SendAsync(ws, ping, WebSocketMessageType.Text, ct).ConfigureAwait(false);
        }
    }

    private async Task SendAsync(ClientWebSocket ws, ReadOnlyMemory<byte> data, WebSocketMessageType type, CancellationToken ct)
    {
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ws.SendAsync(data, type, endOfMessage: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        await _stop.CancelAsync().ConfigureAwait(false);
        try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        foreach (var s in _streams.Values) s.Dispose();
        _streams.Clear();
        _stop.Dispose();
    }

    private sealed class RelayRefusedException(int code) : Exception($"The relay refused this host ({code}).")
    {
        public int Code { get; } = code;
    }

    /// <summary>Bytes from the device waiting to be written to the local connection.</summary>
    private sealed class StreamPipe : IDisposable
    {
        private readonly Channel<byte[]> _channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(32)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        private readonly CancellationTokenSource _cts = new();

        public StreamPipe(uint id) => Id = id;

        public uint Id { get; }
        public CancellationToken Token => _cts.Token;
        public ChannelReader<byte[]> Reader => _channel.Reader;

        public bool TryEnqueue(byte[] payload) => _channel.Writer.TryWrite(payload);

        public void Dispose()
        {
            _channel.Writer.TryComplete();
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class RelayJson : System.Text.Json.Serialization.JsonSerializerContext
{
}
