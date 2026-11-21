using Portal.AiHost.Core.Abstractions;

namespace Portal.AiHost.Engine;

/// <summary>Loopback endpoint and key chosen for one engine launch.</summary>
public sealed record EngineEndpoint(int Port, string ApiKey)
{
    public const string Host = "127.0.0.1";
    public Uri BaseAddress => new($"http://{Host}:{Port}/");
}

/// <summary>A resolved engine executable: flavour name (vulkan, cpu) and its path.</summary>
public sealed record EngineBinary(string Flavour, string ExePath);

/// <summary>
/// Per engine kind knowledge: how to build the argument list and which HTTP routes the engine
/// serves. <see cref="EngineSupervisor"/> is generic over it, so a whisper.cpp engine is a new
/// adapter, not a new supervisor.
/// </summary>
public interface IEngineAdapter
{
    string EngineKind { get; }
    string HealthPath { get; }
    string ChatPath { get; }
    string EmbeddingsPath { get; }
    string TokenizePath { get; }
    string ApplyTemplatePath { get; }

    /// <summary>Argument list for one launch. Must bind loopback only.</summary>
    IReadOnlyList<string> BuildArgs(EngineLaunchSpec spec, EngineEndpoint endpoint);
}
