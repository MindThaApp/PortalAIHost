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
/// Reads the host's command line. Only these protocol links are understood: <c>portal-ai-host:start</c>,
/// <c>portal-ai-host:open</c> and the page links <c>portal-ai-host:settings/performance</c>,
/// <c>portal-ai-host:models</c>, <c>portal-ai-host:apps</c> and <c>portal-ai-host:try</c>. Anything else (other protocol paths, query strings,
/// extra text) is treated as a plain start; nothing from the command line is ever run or opened.
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
                _ when PageFor([raw]) is not null => LaunchMode.Open,
                _ => LaunchMode.Start,
            };
        }

        // No protocol link: a plain start. A second start of an already running host still shows its window.
        return args.Count == 0 ? LaunchMode.Open : LaunchMode.Start;
    }

    /// <summary>The window page a protocol link asks for ("settings/performance", "models", "apps", "try" which opens the Try it page), or null.</summary>
    public static string? PageFor(IReadOnlyList<string> args)
    {
        foreach (var raw in args)
        {
            var a = raw.Trim().Trim('"').TrimEnd('/');
            if (!a.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var link = a[Scheme.Length..].TrimStart('/').ToLowerInvariant();
            if (ModelsPageFor(link) is { } modelsPage)
            {
                return modelsPage;
            }

            return link switch
            {
                "settings/performance" => "settings/performance",
                "models" => "models",
                "apps" => "apps",
                "try" => "tryit",
                _ => null,
            };
        }

        return null;
    }

    /// <summary>
    /// "models?category=image" becomes "models/image" (the Models page filtered to that category). The category is only
    /// passed on when it is a plain id; the window checks it against the known categories, and an unknown one shows the
    /// generic page. Any other query on the models link opens the generic page; null for any other link.
    /// </summary>
    private static string? ModelsPageFor(string link)
    {
        const string head = "models?";
        if (!link.StartsWith(head, StringComparison.Ordinal))
        {
            return null;
        }

        var query = link[head.Length..];
        const string key = "category=";
        return query.StartsWith(key, StringComparison.Ordinal) && query[key.Length..] is { Length: > 0 and <= 32 } id &&
            id.All(ch => ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_')
            ? "models/" + id
            : "models";
    }

    public static bool IsProtocol(IReadOnlyList<string> args) =>
        args.Any(a => a.Trim().Trim('"').StartsWith(Scheme, StringComparison.OrdinalIgnoreCase));
}
