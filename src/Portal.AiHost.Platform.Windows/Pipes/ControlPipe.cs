using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;

namespace Portal.AiHost.Platform.Windows.Pipes;

/// <summary>
/// The admin pipe <c>\\.\pipe\PortalAIHost.Control.&lt;UserSID&gt;</c>. Pipe names are machine-wide, so the SID keeps
/// users apart; the pipe is also created <c>CurrentUserOnly</c>. Framing: one UTF-8 JSON envelope per line.
/// </summary>
public static class ControlPipe
{
    public const string Prefix = "PortalAIHost.Control.";

    /// <summary>Largest accepted line (a settings or Try it payload is far smaller).</summary>
    public const int MaxLineBytes = 4 * 1024 * 1024;

    /// <summary>The pipe name for the current user; <paramref name="suffix"/> separates test hosts.</summary>
    public static string NameForCurrentUser(string? suffix = null)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ??
            throw new InvalidOperationException("The current user has no SID.");
        return suffix is null ? Prefix + sid : Prefix + sid + "." + suffix;
    }

    internal static async Task WriteAsync(PipeStream pipe, ControlEnvelope envelope, SemaphoreSlim gate, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, AiHostJsonContext.Default.ControlEnvelope);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await pipe.WriteAsync(bytes, ct).ConfigureAwait(false);
            await pipe.WriteAsync("\n"u8.ToArray(), ct).ConfigureAwait(false);
            await pipe.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Reads one line; null at end of stream. Over-long lines end the connection.</summary>
    internal static async Task<ControlEnvelope?> ReadAsync(PipeStream pipe, MemoryStream pending, byte[] buffer, CancellationToken ct)
    {
        while (true)
        {
            var data = pending.GetBuffer();
            var newline = Array.IndexOf(data, (byte)'\n', 0, (int)pending.Length);
            if (newline >= 0)
            {
                var line = data.AsSpan(0, newline).ToArray();
                var rest = data.AsSpan(newline + 1, (int)pending.Length - newline - 1).ToArray();
                pending.SetLength(0);
                pending.Write(rest);
                if (line.Length == 0)
                {
                    continue;
                }

                try
                {
                    return JsonSerializer.Deserialize(line, AiHostJsonContext.Default.ControlEnvelope);
                }
                catch (JsonException)
                {
                    return new ControlEnvelope { Type = "" };
                }
            }

            if (pending.Length > MaxLineBytes)
            {
                throw new InvalidDataException("A control message was too long.");
            }

            var n = await pipe.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n == 0)
            {
                return null;
            }

            pending.Write(buffer, 0, n);
        }
    }

    public static ControlEnvelope Ok(string? id, JsonElement? payload = null) =>
        new() { Id = id, Type = ControlTypes.Ok, Payload = payload };

    public static ControlEnvelope Error(string? id, string code, string message) => new()
    {
        Id = id,
        Type = ControlTypes.Error,
        Payload = JsonSerializer.SerializeToElement(new ControlError { Code = code, Message = message },
            AiHostJsonContext.Default.ControlError),
    };
}

/// <summary>Raised by <see cref="ControlPipeClient"/> when the host answers with an error envelope.</summary>
public sealed class ControlPipeException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
