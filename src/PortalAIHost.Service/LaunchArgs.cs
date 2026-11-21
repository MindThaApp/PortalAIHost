namespace PortalAIHost.Service;

/// <summary>What a host start was asked to do.</summary>
public enum LaunchMode
{
    /// <summary>Start (or keep) the host without opening the window.</summary>
    Start,

    /// <summary>Start the host and show the window.</summary>
    Open,
}

/// <summary>
/// Reads the host's command line. Only two protocol links are understood: <c>portal-ai-host:start</c> and
/// <c>portal-ai-host:open</c>. Anything else (other protocol paths, query strings, extra text) is treated as a
/// plain start; nothing from the command line is ever run or opened.
/// </summary>
internal static class LaunchArgs
{
    public const string Scheme = "portal-ai-host:";

    public static LaunchMode Parse(IReadOnlyList<string> args)
    {
        foreach (var raw in args)
        {
            var a = raw.Trim().Trim('"').TrimEnd('/');
            if (!a.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return a[Scheme.Length..].TrimStart('/').ToLowerInvariant() switch
            {
                "open" => LaunchMode.Open,
                _ => LaunchMode.Start,
            };
        }

        // No protocol link: a plain start. A second start of an already running host still shows its window.
        return args.Count == 0 ? LaunchMode.Open : LaunchMode.Start;
    }

    public static bool IsProtocol(IReadOnlyList<string> args) =>
        args.Any(a => a.Trim().Trim('"').StartsWith(Scheme, StringComparison.OrdinalIgnoreCase));
}
