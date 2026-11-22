using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Portal.AiHost.Client;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Store;
using Portal.AiHost.Platform.Windows.Pipes;

namespace PortalAIHost.Soak;

/// <summary>
/// Soak driver for the packaged host (architecture section 7). Runs a TEMP copy of the host exe against a marked
/// test data root, drives three simulated apps through the real client library, and writes a result file.
/// Last stdout line (and last result-file line) is PASS or FAIL with the criteria.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var o = Options.Parse(args);
        if (o is null)
        {
            Console.Error.WriteLine(Options.Usage);
            return 2;
        }

        var result = Soak.RunAsync(o).GetAwaiter().GetResult();
        File.WriteAllText(o.Out, result.Report);
        Console.WriteLine(result.Line);
        return result.Pass ? 0 : 1;
    }
}

internal sealed class Options
{
    public const string Usage =
        "usage: PortalAIHost.Soak --service <PortalAIHost.Service.exe> --out <result file> [--mode fake|real] [--minutes N]\n" +
        "  fake: --engine-stub <dir with llama-server.exe stub and its files>   (routine, default 5 min)\n" +
        "  real: --engine-dir <dir with the real llama-server.exe> --model <chat .gguf> [--embed-model <.gguf>]   (default 60 min)";

    public string Service = "", Out = "", Mode = "fake", EngineDir = "", Model = "", EmbedModel = "";
    public double Minutes;
    public int IdleSeconds, ActiveSeconds, SampleSeconds;

    public static Options? Parse(string[] a)
    {
        string? Get(string n) { var i = Array.IndexOf(a, n); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
        var o = new Options { Service = Get("--service") ?? "", Out = Get("--out") ?? "", Mode = Get("--mode") ?? "fake" };
        if (o.Service.Length == 0 || o.Out.Length == 0 || (o.Mode != "fake" && o.Mode != "real")) return null;
        var fake = o.Mode == "fake";
        o.EngineDir = Get(fake ? "--engine-stub" : "--engine-dir") ?? "";
        o.Model = Get("--model") ?? "";
        o.EmbedModel = Get("--embed-model") ?? "";
        if (o.EngineDir.Length == 0 || (!fake && o.Model.Length == 0)) return null;
        o.Minutes = double.Parse(Get("--minutes") ?? (fake ? "5" : "60"), CultureInfo.InvariantCulture);
        o.IdleSeconds = fake ? 10 : 60;
        o.ActiveSeconds = fake ? 30 : 180;
        o.SampleSeconds = fake ? 5 : 30;
        return o;
    }
}

internal sealed record SoakResult(bool Pass, string Line, string Report);

internal sealed record Sample(double Minute, long PrivateBytes, int Handles, int Threads, int Engines);

internal static class Soak
{
    private const string Marker = ".portal-ai-host-test-root";
    private static readonly string[] Words = ["alpha", "beta", "gamma", "delta", "kappa", "sigma"];

    public static async Task<SoakResult> RunAsync(Options o)
    {
        var log = new StringBuilder();
        void Log(string s) => log.AppendLine($"{DateTime.Now:HH:mm:ss} {s}");
        // A folder this run creates, recreated right before use (the temp cleaner can remove empty folders).
        var root = Path.Combine(Path.GetTempPath(), "portalaihost-soak-" + Guid.NewGuid().ToString("N"));
        Process? host = null;
        var failures = new List<string>();
        var samples = new List<Sample>();
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, Marker), "soak");
            var bin = Path.Combine(root, "bin");
            Directory.CreateDirectory(bin);
            var serviceExe = Path.Combine(bin, "PortalAIHost.Service.exe");
            File.Copy(o.Service, serviceExe);
            CopyDir(o.EngineDir, Path.Combine(bin, "engine", "cpu"));
            if (!File.Exists(Path.Combine(bin, "engine", "cpu", "llama-server.exe")))
                return Fail(log, "engine folder has no llama-server.exe");

            var data = Path.Combine(root, "data");
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, Marker), "soak data root");
            var fake = o.Mode == "fake";
            await SeedAsync(data, "soak-chat", ModelKinds.Chat, fake ? null : o.Model);
            var haveEmbed = fake || o.EmbedModel.Length > 0;
            if (haveEmbed) await SeedAsync(data, "soak-embed", ModelKinds.Embedding, fake ? null : o.EmbedModel);

            var psi = new ProcessStartInfo(serviceExe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = bin };
            psi.Environment[HostPathsVars.TestRoot] = data;
            var pipeName = ControlPipe.NameForCurrentUser("test-" + Hash(Path.GetFullPath(data)));
            // Saved settings apply at the next start: set them, quit, start again.
            host = Process.Start(psi)!;
            await using (var first = await ControlPipeClient.ConnectAsync(pipeName, TimeSpan.FromSeconds(30)))
            {
                var settings = await first.GetSettingsAsync();
                var kinds = new Dictionary<string, KindSettings>
                {
                    [ModelKinds.Chat] = new() { KeepLoaded = false, IdleUnloadSeconds = o.IdleSeconds },
                    [ModelKinds.Embedding] = new() { KeepLoaded = false, IdleUnloadSeconds = o.IdleSeconds },
                };
                await first.SetSettingsAsync(new HostSettings { Backend = "cpu", MaxMemoryPercent = settings.MaxMemoryPercent, Kinds = kinds });
                await first.QuitAsync();
            }
            if (!host.WaitForExit(15000)) throw new InvalidOperationException("host did not quit after settings were saved");
            host = Process.Start(psi)!;
            await using var pipe = await ControlPipeClient.ConnectAsync(pipeName, TimeSpan.FromSeconds(30));
            var status = await pipe.GetStatusAsync();
            var baseUrl = status.BaseUrl ?? throw new InvalidOperationException("host reported no base url");
            Log($"host pid {host.Id} at {baseUrl}");            await pipe.ActivateModelAsync("soak-chat");
            if (haveEmbed) await pipe.ActivateModelAsync("soak-embed");

            // Three simulated apps, approved over the admin pipe.
            var apps = new List<(string Name, AiHostClient Client)>();
            foreach (var n in new[] { "soak-interactive", "soak-batch", "soak-embed-app" })
            {
                if (n == "soak-embed-app" && !haveEmbed) continue;
                apps.Add((n, AiHostClient.Create(new AiHostClientOptions
                {
                    AppId = "com.portalsoak." + n["soak-".Length..].Replace("-", ""), AppDisplayName = n, TokenStore = new MemoryTokenStore(),
                    DiscoveryOverride = new Uri(baseUrl), AccessReason = "soak",
                })));
            }
            using var approveCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var approver = Task.Run(async () =>
            {
                while (!approveCts.IsCancellationRequested)
                {
                    foreach (var p in (await pipe.ListApprovalsAsync(approveCts.Token)).Pending) await pipe.ApproveAsync(p.RequestId, approveCts.Token);
                    await Task.Delay(300, approveCts.Token);
                }
            });
            foreach (var (n, c) in apps)
            {
                var r = await c.RequestAccessAsync(wait: true);
                if (r.State != AiHostState.Ready && r.State != AiHostState.NoModel) failures.Add($"{n}: access ended in {r.State}");
            }
            approveCts.Cancel();
            try { await approver; } catch (OperationCanceledException) { }

            // Load.
            var total = TimeSpan.FromMinutes(o.Minutes);
            var clock = Stopwatch.StartNew();
            var counts = new Counters();
            var ttft = new List<double>();
            var load = Task.Run(() => LoadAsync(apps, o, clock, total, counts, ttft, Log));
            var killTimes = new[] { total * 0.33, total * 0.66 };
            var killed = 0;
            var maxEngines = 0;
            var unloadChecked = 0;
            var nextSample = TimeSpan.Zero;
            while (clock.Elapsed < total)
            {
                if (host.HasExited) { failures.Add($"host exited early with code {host.ExitCode}"); break; }
                if (clock.Elapsed >= nextSample)
                {
                    host.Refresh();
                    var eng = Engines(bin);
                    maxEngines = Math.Max(maxEngines, eng.Length);
                    samples.Add(new Sample(clock.Elapsed.TotalMinutes, host.PrivateMemorySize64, host.HandleCount, host.Threads.Count, eng.Length));
                    nextSample += TimeSpan.FromSeconds(o.SampleSeconds);
                }
                if (killed < killTimes.Length && clock.Elapsed >= killTimes[killed] && Engines(bin).Length > 0)
                {
                    foreach (var e in Engines(bin).Take(1)) { Log($"injected engine kill pid {e.Id}"); e.Kill(); }
                    killed++;
                }
                // Idle phase: once the loop has been quiet for longer than the idle timeout, no engine may remain.
                var quiet = counts.QuietFor(clock);
                if (quiet > TimeSpan.FromSeconds(o.IdleSeconds + 20) && !counts.UnloadChecked)
                {
                    counts.UnloadChecked = true;
                    unloadChecked++;
                    if (Engines(bin).Length > 0) failures.Add($"engine still running {quiet.TotalSeconds:F0}s after the last request (idle unload {o.IdleSeconds}s)");
                }
                await Task.Delay(1000);
            }
            counts.Stop = true;
            await load;
            if (killed < 2) Log($"only {killed} of 2 engine kills injected (no engine was running at the time)");

            // End: quit through the pipe, then no engine and no host may remain.
            try { await pipe.QuitAsync(); } catch (Exception ex) when (ex is IOException or ControlPipeException or ObjectDisposedException) { }
            if (!host.WaitForExit(15000)) { failures.Add("host did not exit after quit"); host.Kill(true); }
            await Task.Delay(1500);
            if (Engines(bin).Length > 0) { failures.Add("engine left after quit"); foreach (var e in Engines(bin)) e.Kill(); }
            foreach (var (_, c) in apps) c.Dispose();

            // Criteria.
            // Skip the warm-up: the model and caches settle in the first ~25 minutes of a real run (the one-time
            // allocator/JIT step seen in earlier runs landed between minute 13 and 25 and then stayed flat).
            // The slope is a Theil-Sen median of pairwise slopes, so a single residual step cannot dominate it.
            var from = Math.Min(25.0, o.Minutes * 0.42);
            var steady = samples.Where(s => s.Minute >= from).ToList();
            var slopeMbPerHour = steady.Count >= 3 ? TheilSen(steady) * 60 / (1024 * 1024) : double.NaN;
            var handleGrowth = steady.Count >= 2 ? steady[^1].Handles - steady[0].Handles : 0;
            var p95 = ttft.Count > 0 ? Percentile(ttft, 0.95) : double.NaN;
            var slopeLimit = fake ? 60.0 : 5.0; // a 5-minute run cannot resolve 5 MB/h; the real 60-minute run holds the 5 MB/h line
            if (double.IsNaN(slopeMbPerHour) || slopeMbPerHour >= slopeLimit) failures.Add($"private-bytes slope {slopeMbPerHour:F2} MB/h (limit {slopeLimit})");
            if (handleGrowth > 50) failures.Add($"handles grew by {handleGrowth} (limit 50)");
            if (counts.Unexpected > 0) failures.Add($"{counts.Unexpected} requests ended in an unexpected state");
            if (counts.Ok == 0) failures.Add("no request succeeded");
            if (double.IsNaN(p95)) failures.Add("interactive time-to-first-token was not measured (no warm streamed token)");
            else if (p95 > 2.0) failures.Add($"interactive p95 time-to-first-token {p95:F2}s (limit 2)");
            if (unloadChecked == 0 && o.Minutes * 60 > o.ActiveSeconds + o.IdleSeconds + 60) failures.Add("no idle-unload window was observed");
            var pass = failures.Count == 0;
            var line = $"{(pass ? "PASS" : "FAIL")} mode={o.Mode} min={o.Minutes} slope={slopeMbPerHour:F2}MB/h handles+{handleGrowth} " +
                       $"req ok={counts.Ok} cancelled={counts.Cancelled} typed-errors={counts.Typed} unexpected={counts.Unexpected} " +
                       $"ttft-p95={p95:F2}s ttft-n={ttft.Count} kills={killed} maxEngines={maxEngines}" + (pass ? "" : " :: " + string.Join("; ", failures));
            Log(line);
            var rep = new StringBuilder(log.ToString());
            rep.AppendLine("minute,private_bytes,handles,threads,engines");
            foreach (var s in samples) rep.AppendLine(FormattableString.Invariant($"{s.Minute:F2},{s.PrivateBytes},{s.Handles},{s.Threads},{s.Engines}"));
            rep.AppendLine(line);
            if (pass) TryDeleteRoot(root);
            return new SoakResult(pass, line, rep.ToString());
        }
        catch (Exception ex)
        {
            return Fail(log, "driver error: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            if (host is { HasExited: false }) { try { host.Kill(true); } catch (InvalidOperationException) { } }
            foreach (var e in Engines(Path.Combine(root, "bin"))) { try { e.Kill(); } catch (InvalidOperationException) { } }
        }
    }

    private static SoakResult Fail(StringBuilder log, string why)
    {
        var line = "FAIL " + why;
        log.AppendLine(line);
        return new SoakResult(false, line, log.ToString());
    }

    private sealed class Counters
    {
        public int Ok, Cancelled, Typed, Unexpected;
        public volatile bool Stop;
        public bool UnloadChecked;
        private long _lastActiveTicks = Stopwatch.GetTimestamp();
        public void Touch() { Interlocked.Exchange(ref _lastActiveTicks, Stopwatch.GetTimestamp()); UnloadChecked = false; }
        public TimeSpan QuietFor(Stopwatch _) => Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastActiveTicks));
    }

    private static async Task LoadAsync(List<(string Name, AiHostClient Client)> apps, Options o, Stopwatch clock, TimeSpan total,
        Counters c, List<double> ttft, Action<string> log)
    {
        var rnd = new Random(1234);
        var cycle = TimeSpan.FromSeconds(o.ActiveSeconds + o.IdleSeconds + 30);
        while (!c.Stop && clock.Elapsed < total)
        {
            var phaseEnd = clock.Elapsed + TimeSpan.FromSeconds(o.ActiveSeconds);
            var cold = true;
            var workers = apps.Select(a => Task.Run(async () =>
            {
                while (!c.Stop && clock.Elapsed < phaseEnd && clock.Elapsed < total)
                {
                    using var cts = new CancellationTokenSource();
                    if (rnd.NextDouble() < 0.10) cts.CancelAfter(TimeSpan.FromMilliseconds(100 + rnd.Next(1500)));
                    var prompt = string.Join(' ', Enumerable.Range(0, 6).Select(_ => Words[rnd.Next(Words.Length)]));
                    var interactive = a.Name == "soak-interactive";
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        c.Touch();
                        if (a.Name == "soak-embed-app")
                        {
                            await a.Client.EmbedAsync([prompt], null, cts.Token);
                        }
                        else
                        {
                            var req = new ChatCompletionRequest { Messages = [new ChatMessage { Role = ChatRoles.User, Content = prompt }], MaxTokens = 16 };
                            var opt = new RequestOptions { Priority = interactive ? AiPriority.Interactive : AiPriority.Batch, Deadline = TimeSpan.FromMinutes(5) };
                            if (interactive)
                            {
                                var first = true;
                                await foreach (var d in a.Client.StreamChatAsync(req, opt, cts.Token))
                                {
                                    // A reasoning model (Qwen3) streams its first tokens as reasoning text, so either kind is
                                    // the first token; progress comments carry neither.
                                    if (first && (d.Text is { Length: > 0 } || d.ReasoningText is { Length: > 0 }))
                                    {
                                        first = false;
                                        if (!cold) lock (ttft) ttft.Add(sw.Elapsed.TotalSeconds);
                                        cold = false;
                                    }
                                }
                            }
                            else
                            {
                                await a.Client.ChatAsync(req, opt, cts.Token);
                            }
                        }
                        Interlocked.Increment(ref c.Ok);
                    }
                    catch (OperationCanceledException) { Interlocked.Increment(ref c.Cancelled); }
                    catch (AiHostException ex) when (ex.Code is not (ClientErrorCodes.HostUnavailable or ClientErrorCodes.NoAccess))
                    {
                        Interlocked.Increment(ref c.Typed);
                        cold = true; // an engine restart or load follows
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref c.Unexpected);
                        log($"unexpected {a.Name}: {ex.GetType().Name} {ex.Message}");
                    }
                    c.Touch();
                    await Task.Delay(interactive ? 200 : 50);
                }
            }));
            await Task.WhenAll(workers);
            // Quiet phase, long enough for the idle unload to fire and be checked.
            var quietEnd = clock.Elapsed + (cycle - TimeSpan.FromSeconds(o.ActiveSeconds));
            while (!c.Stop && clock.Elapsed < quietEnd && clock.Elapsed < total) await Task.Delay(500);
        }
    }

    private static Process[] Engines(string bin)
    {
        // Matched by full image path: only llama-server.exe inside this run's own folder.
        var own = Path.GetFullPath(bin) + Path.DirectorySeparatorChar;
        var list = new List<Process>();
        foreach (var p in Process.GetProcessesByName("llama-server"))
        {
            try
            {
                var path = p.MainModule?.FileName;
                if (path is not null && path.StartsWith(own, StringComparison.OrdinalIgnoreCase)) list.Add(p);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }
        return [.. list];
    }

    private static async Task SeedAsync(string data, string id, string kind, string? sourceGguf)
    {
        // Put the model into the host's own store through the store API, never by touching the source file.
        var store = ModelStore.OpenOrCreate(Path.Combine(data, "models"));
        await store.EnsureModelAsync(id, id, kind, "llama-server", CancellationToken.None);
        var part = await store.BeginFileAsync(id, id + ".gguf", CancellationToken.None);
        await using (var fs = await store.OpenPartForWriteAsync(id, part, 81920, CancellationToken.None))
        {
            if (sourceGguf is null) fs.Write(MinimalGguf());
            else { await using var src = new FileStream(sourceGguf, FileMode.Open, FileAccess.Read, FileShare.Read); await src.CopyToAsync(fs); }
        }
        string sha;
        await using (var rd = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read)) sha = Convert.ToHexString(await SHA256.HashDataAsync(rd)).ToLowerInvariant();
        await store.CommitFileAsync(id, part, sha, CancellationToken.None);
    }

    private static byte[] MinimalGguf()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(0x46554747u); w.Write(3u); w.Write(0UL); w.Write(1UL);
        void Str(string s) { var b = Encoding.UTF8.GetBytes(s); w.Write((ulong)b.Length); w.Write(b); }
        Str("general.architecture"); w.Write(8u); Str("llama");
        w.Write(new byte[64]);
        return ms.ToArray();
    }

    private static string Hash(string full) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToUpperInvariant())))[..16];

    private static double TheilSen(List<Sample> s)
    {
        var slopes = new List<double>();
        for (var i = 0; i < s.Count; i++)
            for (var j = i + 1; j < s.Count; j++)
                if (s[j].Minute > s[i].Minute) slopes.Add((s[j].PrivateBytes - s[i].PrivateBytes) / (s[j].Minute - s[i].Minute));
        if (slopes.Count == 0) return double.NaN;
        slopes.Sort();
        return slopes[slopes.Count / 2];
    }

    private static double Slope(List<Sample> s)
    {
        var n = s.Count; var mx = s.Average(x => x.Minute); var my = s.Average(x => (double)x.PrivateBytes);
        var num = s.Sum(x => (x.Minute - mx) * (x.PrivateBytes - my)); var den = s.Sum(x => (x.Minute - mx) * (x.Minute - mx));
        return den == 0 ? 0 : num / den; // bytes per minute
    }

    private static double Percentile(List<double> v, double p)
    {
        double[] a; lock (v) a = [.. v.Order()];
        return a[Math.Min(a.Length - 1, (int)Math.Ceiling(p * a.Length) - 1)];
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: false);
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    /// <summary>Deletes only a folder this run created: under %TEMP%, named portalaihost-soak-*, holding the marker.</summary>
    private static void TryDeleteRoot(string root)
    {
        var full = Path.GetFullPath(root);
        if (File.Exists(Path.Combine(full, Marker)) && Path.GetFileName(full).StartsWith("portalaihost-soak-", StringComparison.Ordinal) &&
            full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        {
            try { Directory.Delete(full, recursive: true); } catch (IOException) { }
        }
    }
}

internal static class HostPathsVars
{
    public const string TestRoot = "PORTALAIHOST_TEST_DATA_ROOT";
}
