using System.Globalization;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;

namespace Portal.AiHost.Engine;

/// <summary>Optional extras for the llama-server argument list.</summary>
public sealed record LlamaServerArgsOptions
{
    /// <summary>Value for --override-tensor (for example "exps=CPU"), for MoE models on small GPUs.</summary>
    public string? OverrideTensor { get; init; }

    /// <summary>Extra flags appended last. They may not change the bind address or the key.</summary>
    public IReadOnlyList<string> ExtraArgs { get; init; } = Array.Empty<string>();
}

/// <summary>Adapter for the bundled llama.cpp llama-server.</summary>
public sealed class LlamaServerAdapter : IEngineAdapter
{
    private readonly LlamaServerArgsOptions _options;

    public LlamaServerAdapter(LlamaServerArgsOptions? options = null)
    {
        _options = options ?? new LlamaServerArgsOptions();
    }

    public string EngineKind => EngineKinds.LlamaServer;
    public string HealthPath => "/health";
    public string ChatPath => "/v1/chat/completions";
    public string EmbeddingsPath => "/v1/embeddings";
    public string TokenizePath => "/tokenize";
    public string ApplyTemplatePath => "/apply-template";

    public IReadOnlyList<string> BuildArgs(EngineLaunchSpec spec, EngineEndpoint endpoint) =>
        LlamaServerArgs.Build(spec, endpoint, _options);
}

/// <summary>Builds and validates the llama-server command line.</summary>
public static class LlamaServerArgs
{
    private static readonly string[] ForbiddenValues = { "0.0.0.0", "::", "[::]", "*" };
    private static readonly string[] ProtectedFlags = { "--host", "--port", "--api-key", "--api-key-file", "-m", "--model" };

    public static IReadOnlyList<string> Build(EngineLaunchSpec spec, EngineEndpoint endpoint, LlamaServerArgsOptions? options = null)
    {
        options ??= new LlamaServerArgsOptions();
        if (spec.ModelFiles.Count == 0)
        {
            throw new ArgumentException("A model file is required.", nameof(spec));
        }

        var p = spec.Placement;
        var cpu = string.Equals(p.Backend, "cpu", StringComparison.OrdinalIgnoreCase);
        var slots = Math.Max(1, p.ParallelSlots);
        var args = new List<string>
        {
            "--host", EngineEndpoint.Host,
            "--port", endpoint.Port.ToString(CultureInfo.InvariantCulture),
            "--api-key", endpoint.ApiKey,
            "--no-webui",
            // Only the first part of a split GGUF is passed; llama.cpp finds the other parts itself.
            "-m", spec.ModelFiles[0],
            // llama-server divides --ctx-size across the parallel slots, so ask for the total.
            "--ctx-size", (Math.Max(0, p.ContextTokens) * slots).ToString(CultureInfo.InvariantCulture),
            "--parallel", slots.ToString(CultureInfo.InvariantCulture),
            "--n-gpu-layers", (cpu ? 0 : Math.Max(0, p.GpuLayers)).ToString(CultureInfo.InvariantCulture),
        };

        if (spec.Threads is int threads && threads > 0)
        {
            args.Add("--threads");
            args.Add(threads.ToString(CultureInfo.InvariantCulture));
        }

        if (p.CpuMoeLayers is int moe && moe > 0)
        {
            args.Add("--n-cpu-moe");
            args.Add(moe.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(options.OverrideTensor))
        {
            args.Add("--override-tensor");
            args.Add(options.OverrideTensor!);
        }

        if (!string.IsNullOrWhiteSpace(p.KvType) && !string.Equals(p.KvType, "f16", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("--cache-type-k");
            args.Add(p.KvType);
            args.Add("--cache-type-v");
            args.Add(p.KvType);
        }

        if (spec.Kind == ModelKinds.Embedding)
        {
            args.Add("--embeddings");
        }

        if (spec.SupportsTools || spec.SupportsReasoning)
        {
            args.Add("--jinja");
        }

        foreach (var extra in options.ExtraArgs)
        {
            args.Add(extra);
        }

        AssertLoopbackOnly(args);
        return args;
    }

    /// <summary>
    /// Throws unless the argument list binds 127.0.0.1 exactly once and carries no wildcard
    /// address, no repeated bind or key flag and no environment override of them.
    /// </summary>
    public static void AssertLoopbackOnly(IReadOnlyList<string> args)
    {
        var hostCount = 0;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            foreach (var bad in ForbiddenValues)
            {
                if (string.Equals(a, bad, StringComparison.Ordinal) || a.EndsWith("=" + bad, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Engine arguments must not contain the wildcard address '{bad}'.");
                }
            }

            if (a == "--host" || a.StartsWith("--host=", StringComparison.Ordinal))
            {
                hostCount++;
                var value = a.StartsWith("--host=", StringComparison.Ordinal) ? a[7..] : (i + 1 < args.Count ? args[i + 1] : "");
                if (value != EngineEndpoint.Host)
                {
                    throw new InvalidOperationException("Engine must bind 127.0.0.1 only.");
                }
            }
        }

        if (hostCount != 1)
        {
            throw new InvalidOperationException("Engine arguments must contain exactly one --host 127.0.0.1.");
        }

        foreach (var flag in ProtectedFlags)
        {
            var n = args.Count(x => x == flag || x.StartsWith(flag + "=", StringComparison.Ordinal));
            if (n > 1)
            {
                throw new InvalidOperationException($"Engine argument {flag} may appear only once.");
            }
        }
    }
}
