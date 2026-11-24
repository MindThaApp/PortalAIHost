using System.Collections.Concurrent;
using System.IO.Pipes;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;

namespace Portal.AiHost.Platform.Windows.Pipes;

/// <summary>Handles one control request and returns the answer envelope (same Id).</summary>
public delegate Task<ControlEnvelope> ControlRequestHandler(ControlEnvelope request, CancellationToken ct);

/// <summary>Who holds a pipe name that this host could not create.</summary>
public enum PipeHolder
{
    /// <summary>Nobody answers on the name.</summary>
    None,

    /// <summary>Another host of the current user.</summary>
    CurrentUser,

    /// <summary>A process of another Windows user created the name first.</summary>
    OtherUser,
}

/// <summary>
/// Host side of the admin pipe. The first pipe instance is created with <c>FirstPipeInstance | CurrentUserOnly</c>,
/// which makes it the single-instance lock: while this host lives, at least one instance always exists (the next
/// instance is created before the current one is served or recycled), so no other process can create the pipe.
/// Pushes (events) go to every connected window through a small per-connection queue.
/// </summary>
public sealed class ControlPipeServer : IAsyncDisposable
{
    /// <summary>Connections served at once; more are closed straight away.</summary>
    public const int MaxConnections = 8;

    /// <summary>Pushes waiting per connection; status is coalesced, the oldest progress is dropped first.</summary>
    public const int MaxQueuedPushes = 32;

    private readonly string _name;
    private readonly ControlRequestHandler _handler;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<Connection, byte> _connections = new();
    private readonly NamedPipeServerStream _first;
    private Task _accept = Task.CompletedTask;
    private int _begun;

    private ControlPipeServer(string name, ControlRequestHandler handler, NamedPipeServerStream first)
    {
        _name = name;
        _handler = handler;
        _first = first;
    }

    public string PipeName => _name;

    public int ConnectionCount => _connections.Count;

    /// <summary>A write (answer or push) that takes longer than this drops the connection.</summary>
    public TimeSpan WriteTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Creates the first pipe instance, or returns null when another process already owns the name. With
    /// <paramref name="acceptNow"/> false the name is held but nothing is answered until <see cref="Begin"/>:
    /// clients that connect meanwhile wait, and their requests are read once the host is ready.
    /// </summary>
    public static ControlPipeServer? TryStart(string pipeName, ControlRequestHandler handler, bool acceptNow = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(pipeName);
        ArgumentNullException.ThrowIfNull(handler);
        NamedPipeServerStream first;
        try
        {
            first = Create(pipeName, firstInstance: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var server = new ControlPipeServer(pipeName, handler, first);
        if (acceptNow)
        {
            server.Begin();
        }

        return server;
    }

    /// <summary>Starts answering; safe to call more than once.</summary>
    public void Begin()
    {
        if (Interlocked.CompareExchange(ref _begun, 1, 0) == 0)
        {
            _accept = Task.Run(() => AcceptLoopAsync(_first));
        }
    }

    /// <summary>Finds out who holds a name that <see cref="TryStart"/> could not create.</summary>
    public static async Task<PipeHolder> ProbeHolderAsync(string pipeName, TimeSpan timeout)
    {
        // CurrentUserOnly makes the client check that the server's owner is this user.
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, System.Security.Principal.TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(timeout, CancellationToken.None).ConfigureAwait(false);
            return PipeHolder.CurrentUser;
        }
        catch (UnauthorizedAccessException)
        {
            return PipeHolder.OtherUser;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            return PipeHolder.None;
        }
    }

    private static NamedPipeServerStream Create(string name, bool firstInstance)
    {
        var options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
        if (firstInstance)
        {
            options |= PipeOptions.FirstPipeInstance;
        }

        return new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, options);
    }

    /// <summary>Creates the next instance, or null after a short back-off (the caller keeps its current one).</summary>
    private async Task<NamedPipeServerStream?> TryCreateNextAsync(CancellationToken ct)
    {
        try
        {
            return Create(_name, firstInstance: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false);
            return null;
        }
    }

    private static bool TryRecycle(NamedPipeServerStream pipe)
    {
        try
        {
            if (pipe.IsConnected)
            {
                pipe.Disconnect();
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            return false;
        }
    }

    private async Task AcceptLoopAsync(NamedPipeServerStream listening)
    {
        var ct = _stop.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await listening.WaitForConnectionAsync(ct).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // The client went away before it was served. Reuse this instance; replace it only once a
                    // new one exists, so the name is never free.
                    if (!TryRecycle(listening) && await TryCreateNextAsync(ct).ConfigureAwait(false) is { } fresh)
                    {
                        await listening.DisposeAsync().ConfigureAwait(false);
                        listening = fresh;
                    }

                    continue;
                }

                // Always have the next listening instance before serving (or closing) this one.
                if (await TryCreateNextAsync(ct).ConfigureAwait(false) is not { } next)
                {
                    TryRecycle(listening);
                    continue;
                }

                var connected = listening;
                listening = next;
                if (_connections.Count >= MaxConnections)
                {
                    await connected.DisposeAsync().ConfigureAwait(false);
                    continue;
                }

                var connection = new Connection(connected, ct, WriteTimeout);
                _connections[connection] = 0;
                _ = ServeAsync(connection);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Keep the name held until disposal even if the loop itself fails.
            await Task.Delay(Timeout.Infinite, ct).ContinueWith(static _ => { }, TaskScheduler.Default).ConfigureAwait(false);
        }
        finally
        {
            await listening.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ServeAsync(Connection c)
    {
        var pending = new MemoryStream();
        var buffer = new byte[16 * 1024];
        var pushes = Task.Run(() => PushLoopAsync(c));
        try
        {
            while (!c.Token.IsCancellationRequested)
            {
                var request = await ControlPipe.ReadAsync(c.Pipe, pending, buffer, c.Token).ConfigureAwait(false);
                if (request is null)
                {
                    break;
                }

                _ = AnswerAsync(c, request);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException or ObjectDisposedException)
        {
        }
        finally
        {
            _connections.TryRemove(c, out _);
            await c.DisposeAsync().ConfigureAwait(false);
            await pushes.ConfigureAwait(false);
            await pending.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task AnswerAsync(Connection c, ControlEnvelope request)
    {
        ControlEnvelope answer;
        if (string.IsNullOrEmpty(request.Type) || string.IsNullOrEmpty(request.Id))
        {
            answer = ControlPipe.Error(request.Id, ErrorCodes.InvalidRequest, "Each request needs an id and a type.");
        }
        else
        {
            try
            {
                // The connection's token: a window that closes cancels its own work (for example Try it).
                answer = await _handler(request, c.Token).ConfigureAwait(false);
                answer = new ControlEnvelope { Id = request.Id, Type = answer.Type, Payload = answer.Payload };
            }
            catch (OperationCanceledException) when (c.Token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                answer = ControlPipe.Error(request.Id, ErrorCodes.InternalError, "The host could not handle the request.");
            }
        }

        await c.WriteOrDropAsync(answer).ConfigureAwait(false);
    }

    private static async Task PushLoopAsync(Connection c)
    {
        try
        {
            while (true)
            {
                var push = await c.NextPushAsync().ConfigureAwait(false);
                if (!await c.WriteOrDropAsync(push).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Queues a push (no Id) for every connected window and returns at once. A window that does not read is
    /// dropped by its write timeout, never slowing the host or the other windows.
    /// </summary>
    public Task BroadcastAsync(string type, System.Text.Json.JsonElement? payload)
    {
        var envelope = new ControlEnvelope { Type = type, Payload = payload };
        foreach (var c in _connections.Keys)
        {
            c.Enqueue(envelope);
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _begun, 2) == 2)
        {
            return;
        }

        try
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await _accept.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Disposal never throws: it runs inside quit and shutdown.
            }

            foreach (var c in _connections.Keys)
            {
                await c.DisposeAsync().ConfigureAwait(false);
            }

            _connections.Clear();
        }
        finally
        {
            await _first.DisposeAsync().ConfigureAwait(false);
            _stop.Dispose();
        }
    }

    private sealed class Connection : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly TimeSpan _writeTimeout;
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly SemaphoreSlim _pushSignal = new(0);
        private readonly LinkedList<ControlEnvelope> _queue = new();
        private readonly Lock _queueGate = new();
        private int _disposed;

        public Connection(NamedPipeServerStream pipe, CancellationToken serverStop, TimeSpan writeTimeout)
        {
            Pipe = pipe;
            _writeTimeout = writeTimeout;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(serverStop);
            Token = _cts.Token;
        }

        public NamedPipeServerStream Pipe { get; }

        /// <summary>Cancelled when this connection ends or the server stops.</summary>
        public CancellationToken Token { get; }

        public void Enqueue(ControlEnvelope push)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            var signal = true;
            lock (_queueGate)
            {
                if (push.Type == ControlTypes.EventStatusChanged && Find(ControlTypes.EventStatusChanged) is { } old)
                {
                    old.Value = push; // Coalesce: only the newest status matters.
                    signal = false;
                }
                else
                {
                    if (_queue.Count >= MaxQueuedPushes)
                    {
                        // Reply text of "Try it" is dropped last: losing it would leave a hole in the answer.
                        var drop = Find(ControlTypes.EventDownloadProgress) ?? Find(ControlTypes.EventEngineState) ??
                            FindNot(ControlTypes.EventTryItChunk) ?? _queue.First!;
                        _queue.Remove(drop);
                        signal = false;
                    }

                    _queue.AddLast(push);
                }
            }

            if (signal)
            {
                _pushSignal.Release();
            }
        }

        private LinkedListNode<ControlEnvelope>? FindNot(string type)
        {
            for (var n = _queue.First; n is not null; n = n.Next)
            {
                if (n.Value.Type != type)
                {
                    return n;
                }
            }

            return null;
        }

        private LinkedListNode<ControlEnvelope>? Find(string type)
        {
            for (var n = _queue.First; n is not null; n = n.Next)
            {
                if (n.Value.Type == type)
                {
                    return n;
                }
            }

            return null;
        }

        public async Task<ControlEnvelope> NextPushAsync()
        {
            while (true)
            {
                await _pushSignal.WaitAsync(Token).ConfigureAwait(false);
                lock (_queueGate)
                {
                    if (_queue.First is { } first)
                    {
                        _queue.RemoveFirst();
                        return first.Value;
                    }
                }
            }
        }

        /// <summary>Writes with a deadline; on failure or timeout the connection is closed. False when dropped.</summary>
        public async Task<bool> WriteOrDropAsync(ControlEnvelope envelope)
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
                deadline.CancelAfter(_writeTimeout);
                await ControlPipe.WriteAsync(Pipe, envelope, _writeGate, deadline.Token).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                await DisposeAsync().ConfigureAwait(false);
                return false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                await _cts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }

            await Pipe.DisposeAsync().ConfigureAwait(false);
        }
    }
}
