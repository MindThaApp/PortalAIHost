using System.Text.Json;
using Portal.AiHost.Contracts.Control;

namespace PortalAIHost.App.Services;

/// <summary>One open connection to the host's control pipe. Raw envelopes only; <see cref="PipeHostControl"/> reads them.</summary>
public interface IControlChannel : IAsyncDisposable
{
    /// <summary>Host pushes (envelopes without an id).</summary>
    event Action<ControlEnvelope>? EventReceived;

    /// <summary>Raised once when the connection ends (host quit, crashed or the pipe broke).</summary>
    event Action? Disconnected;

    /// <summary>
    /// Sends one request and waits for its answer. Error answers throw <see cref="HostRequestException"/>;
    /// a broken connection throws <see cref="HostUnavailableException"/>.
    /// </summary>
    Task<ControlEnvelope> ExchangeAsync(string type, JsonElement? payload, CancellationToken ct);
}

/// <summary>Opens control connections. Returns null when no host answers.</summary>
public interface IControlChannelFactory
{
    Task<IControlChannel?> TryConnectAsync(CancellationToken ct);
}

/// <summary>The host answered a request with an error (for example "not_found" or "in_use").</summary>
public sealed class HostRequestException : Exception
{
    public HostRequestException(string code, string message) : base(message) => Code = code;

    public string Code { get; }
}
