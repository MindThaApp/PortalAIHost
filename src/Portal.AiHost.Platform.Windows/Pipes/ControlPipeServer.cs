using System.Collections.Concurrent;
using System.IO.Pipes;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;

namespace Portal.AiHost.Platform.Windows.Pipes;

/// <summary>Handles one control request and returns the answer envelope (same Id).</summary>
public delegate Task<ControlEnvelope> ControlRequestHandler(ControlEnvelope request, CancellationToken ct);

/// <summary>
/// Host side of the admin pipe. The first pipe instance is created with <c>FirstPipeInstance | CurrentUserOnly</c>,
/// which makes it the single-instance lock: while this host lives, one instance is always listening, so no other
/// process can create the pipe. Pushes (events) go to every connected window.
/// </summary>
public sealed class ControlPipeServer : IAsyncDisposable
{
    private const int MaxConnections = 8;
    private readonly string _name;
    private readonly ControlRequestHandler _handler;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<Connection, byte> _connections = new();
    private Task _accept = Task.CompletedTask;

    private ControlPipeServer(string name, ControlRequestHandler handler)
    {
        _name = name;
        _handler = handler;
    }

    public string PipeName => _name;

    public int ConnectionCount => _connections.Count;

    /// <summary>
    /// Creates the first pipe instance, or returns null when another host for this user already owns the name.
    /// </summary>
    public static ControlPipeServer? TryStart(string pipeName, ControlRequestHandler handler)
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

        var server = new ControlPipeServer(pipeName, handler);
        server._accept = server.AcceptLoopAsync(first);
        return server;
    }

    private static NamedPipeServerStream Create(string name, bool firstInstance)
    {
        var options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
        if (firstInstance)
        {
            options |= PipeOptions.FirstPipeInstance;
        }

        return new NamedPipeServerStream(name, PipeDirection.InOut, MaxConnections, PipeTransmissionMode.Byte, options);
    }

    private async Task AcceptLoopAsync(NamedPipeServerStream listening)
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await listening.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await listening.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (IOException)
            {
                await listening.DisposeAsync().ConfigureAwait(false);
                listening = Create(_name, firstInstance: false);
                continue;
            }

            var connected = listening;
            // Always keep one instance listening before serving, so the name never becomes free.
            listening = Create(_name, firstInstance: false);
            var connection = new Connection(connected);
            _connections[connection] = 0;
            _ = ServeAsync(connection, ct);
        }

        await listening.DisposeAsync().ConfigureAwait(false);
    }

    private async Task ServeAsync(Connection c, CancellationToken ct)
    {
        var pending = new MemoryStream();
        var buffer = new byte[16 * 1024];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var request = await ControlPipe.ReadAsync(c.Pipe, pending, buffer, ct).ConfigureAwait(false);
                if (request is null)
                {
                    break;
                }

                _ = AnswerAsync(c, request, ct);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException or ObjectDisposedException)
        {
        }
        finally
        {
            _connections.TryRemove(c, out _);
            await c.DisposeAsync().ConfigureAwait(false);
            await pending.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task AnswerAsync(Connection c, ControlEnvelope request, CancellationToken ct)
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
                answer = await _handler(request, ct).ConfigureAwait(false);
                answer = new ControlEnvelope { Id = request.Id, Type = answer.Type, Payload = answer.Payload };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                answer = ControlPipe.Error(request.Id, ErrorCodes.InternalError, "The host could not handle the request.");
            }
        }

        try
        {
            await ControlPipe.WriteAsync(c.Pipe, answer, c.WriteGate, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    /// <summary>Sends a push (no Id) to every connected window; a broken connection is dropped.</summary>
    public async Task BroadcastAsync(string type, System.Text.Json.JsonElement? payload)
    {
        var envelope = new ControlEnvelope { Type = type, Payload = payload };
        foreach (var c in _connections.Keys)
        {
            try
            {
                await ControlPipe.WriteAsync(c.Pipe, envelope, c.WriteGate, _stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                _connections.TryRemove(c, out _);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        await _accept.ConfigureAwait(false);
        foreach (var c in _connections.Keys)
        {
            await c.DisposeAsync().ConfigureAwait(false);
        }

        _connections.Clear();
        _stop.Dispose();
    }

    private sealed class Connection(NamedPipeServerStream pipe) : IAsyncDisposable
    {
        public NamedPipeServerStream Pipe { get; } = pipe;

        public SemaphoreSlim WriteGate { get; } = new(1, 1);

        public ValueTask DisposeAsync() => Pipe.DisposeAsync();
    }
}
