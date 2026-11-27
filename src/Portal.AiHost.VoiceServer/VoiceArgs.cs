using System.Globalization;

namespace Portal.AiHost.VoiceServer;

/// <summary>The helper command line, as the Host builds it: --host --port --api-key --model-dir [--threads] [--provider].</summary>
public sealed record VoiceArgs(string Host, int Port, string ApiKey, string ModelDir, int Threads, string Provider)
{
    public const int DefaultThreads = 4;
    public const int MaxThreads = 64;

    private static readonly HashSet<string> Known = ["--host", "--port", "--api-key", "--model-dir", "--threads", "--provider"];

    /// <summary>Parses the arguments. Throws <see cref="ArgumentException"/> with a short message when one is wrong.</summary>
    public static VoiceArgs Parse(IReadOnlyList<string> args)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument '{a}'.");
            }

            string key, value;
            var eq = a.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                key = a[..eq];
                value = a[(eq + 1)..];
            }
            else
            {
                key = a;
                if (i + 1 >= args.Count)
                {
                    throw new ArgumentException($"Missing value for {a}.");
                }

                value = args[++i];
            }

            if (!Known.Contains(key))
            {
                throw new ArgumentException($"Unknown option {key}.");
            }

            if (!map.TryAdd(key, value))
            {
                throw new ArgumentException($"{key} given twice.");
            }
        }

        var host = map.GetValueOrDefault("--host", "127.0.0.1");
        if (host != "127.0.0.1")
        {
            throw new ArgumentException("Only 127.0.0.1 is allowed as host.");
        }

        if (!map.TryGetValue("--port", out var portText)
            || !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
        {
            throw new ArgumentException("--port must be 1-65535.");
        }

        if (!map.TryGetValue("--api-key", out var apiKey) || string.IsNullOrEmpty(apiKey))
        {
            throw new ArgumentException("--api-key is required.");
        }

        if (!map.TryGetValue("--model-dir", out var modelDir) || string.IsNullOrWhiteSpace(modelDir))
        {
            throw new ArgumentException("--model-dir is required.");
        }

        var threads = DefaultThreads;
        if (map.TryGetValue("--threads", out var tt))
        {
            if (!int.TryParse(tt, NumberStyles.None, CultureInfo.InvariantCulture, out threads) || threads < 1)
            {
                throw new ArgumentException("--threads must be a positive number.");
            }

            threads = Math.Min(threads, MaxThreads);
        }

        var provider = map.GetValueOrDefault("--provider", "cpu");
        if (provider != "cpu")
        {
            throw new ArgumentException("Only the cpu provider is supported.");
        }

        return new VoiceArgs(host, port, apiKey, modelDir, threads, provider);
    }
}
