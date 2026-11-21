using Portal.AiHost.Contracts;

namespace Portal.AiHost.Client.Internal;

/// <summary>A validated host address.</summary>
internal sealed record HostEndpoint(Uri BaseAddress, string InstanceId, string Source);

/// <summary>Finds the host: override, then discovery file, then default port (api-v1 section 2).</summary>
internal sealed class HostDiscovery(HttpClient http, AiHostClientOptions options, Action<string> log)
{
    public async Task<HostEndpoint?> DiscoverAsync(CancellationToken ct)
    {
        var overrides = new List<Uri>();
        if (options.DiscoveryOverride is { } o)
        {
            overrides.Add(o);
        }

        var env = Environment.GetEnvironmentVariable(ApiInfo.UrlOverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env) && Uri.TryCreate(env, UriKind.Absolute, out var envUri))
        {
            overrides.Add(envUri);
        }

        foreach (var uri in overrides)
        {
            if (await PingAsync(uri, null, ct).ConfigureAwait(false) is { } ping)
            {
                return new HostEndpoint(Normalize(uri), ping.InstanceId, "override");
            }
        }

        foreach (var file in CandidateFiles())
        {
            var found = await TryFileAsync(file, ct).ConfigureAwait(false);
            if (found is not null)
            {
                return found;
            }
        }

        var def = new Uri($"http://{ApiInfo.DefaultHost}:{options.Discovery.DefaultPort}/");
        if (await PingAsync(def, null, ct).ConfigureAwait(false) is { } p)
        {
            return new HostEndpoint(def, p.InstanceId, "default-port");
        }

        return null;
    }

    /// <summary>True if a host package folder exists, so a failed discovery means "not running", not "not installed".</summary>
    public bool LooksInstalled()
    {
        var root = PackagesRoot();
        foreach (var pfn in options.Discovery.PackageFamilyNames)
        {
            try
            {
                if (Directory.Exists(Path.Combine(root, pfn)))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // treat unreadable as absent
            }
        }

        return false;
    }

    private string PackagesRoot() => options.Discovery.PackagesRoot ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");

    private IEnumerable<string> CandidateFiles()
    {
        var root = PackagesRoot();
        foreach (var pfn in options.Discovery.PackageFamilyNames)
        {
            yield return Path.Combine(root, pfn, "LocalState", "discovery", "host.json");
        }
    }

    private async Task<HostEndpoint?> TryFileAsync(string path, CancellationToken ct)
    {
        DiscoveryFile? file;
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            file = AiHostJson.Deserialize(json, AiHostJsonContext.Default.DiscoveryFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            log("discovery file unreadable");
            return null;
        }

        if (file is null || file.Service != ApiInfo.ServiceName || !Uri.TryCreate(file.BaseUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var ping = await PingAsync(uri, file.InstanceId, ct).ConfigureAwait(false);
        if (ping is null)
        {
            log("discovery file is stale");
            return null;
        }

        return new HostEndpoint(Normalize(uri), ping.InstanceId, "file");
    }

    private async Task<PingResponse?> PingAsync(Uri baseUri, string? expectedInstance, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(options.Discovery.PingTimeout);
        try
        {
            using var resp = await http.GetAsync(new Uri(Normalize(baseUri), "host/ping"), cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var ping = AiHostJson.Deserialize(body, AiHostJsonContext.Default.PingResponse);
            if (ping is null || ping.Service != ApiInfo.ServiceName)
            {
                return null;
            }

            if (expectedInstance is not null && !string.Equals(ping.InstanceId, expectedInstance, StringComparison.Ordinal))
            {
                return null;
            }

            return ping;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException
            && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static Uri Normalize(Uri uri)
    {
        var s = uri.GetLeftPart(UriPartial.Authority) + "/";
        return new Uri(s);
    }
}
