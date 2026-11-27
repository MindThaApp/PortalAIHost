using System.Globalization;
using System.Text.Json;

namespace Portal.AiHost.Engine.Upscale;

/// <summary>How to start the add-on's worker, read from its install marker.</summary>
public sealed record BestTierLaunch(string Exe, IReadOnlyList<string> Args, string WorkDir);

/// <summary>Install state of the Best quality add-on folder. Read only: nothing here writes or deletes.</summary>
public sealed class BestTierInstall(string addonRoot)
{
    public const string MarkerFileName = "install.json";

    public string AddonRoot { get; } = addonRoot;

    /// <summary>Folder the worker's models are read from.</summary>
    public string ModelsDir => Path.Combine(AddonRoot, "models");

    public UpscaleEngineState Check(out BestTierLaunch? launch)
    {
        launch = null;
        var marker = Path.Combine(AddonRoot, MarkerFileName);
        if (!File.Exists(marker))
        {
            return new UpscaleEngineState(UpscaleEngineStatus.NotInstalled, "The Best quality add-on is not installed.");
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(marker));
            var root = doc.RootElement;
            var python = root.TryGetProperty("python", out var p) ? p.GetString() : null;
            if (string.IsNullOrWhiteSpace(python))
            {
                return Broken("The install marker names no Python program.");
            }

            var exe = Path.GetFullPath(python, AddonRoot);
            if (!File.Exists(exe))
            {
                return Broken("The add-on's Python program is missing. Reinstall the add-on.");
            }

            var args = new List<string>();
            if (root.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array)
            {
                args.AddRange(a.EnumerateArray().Select(e => e.GetString() ?? string.Empty));
            }
            else
            {
                args.AddRange(["-m", "portal_best_upscale"]);
            }

            var work = root.TryGetProperty("worker_dir", out var w) && w.GetString() is { Length: > 0 } wd
                ? Path.GetFullPath(wd, AddonRoot)
                : Path.Combine(AddonRoot, "worker");
            if (!Directory.Exists(work))
            {
                return Broken("The add-on's worker folder is missing. Reinstall the add-on.");
            }

            launch = new BestTierLaunch(exe, args, work);
            return new UpscaleEngineState(UpscaleEngineStatus.Ready);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return Broken("The install marker could not be read.");
        }
    }

    private static UpscaleEngineState Broken(string reason) => new(UpscaleEngineStatus.Broken, reason);
}

/// <summary>What installing the add-on involves, shown to the user before anything is downloaded.</summary>
public sealed record BestTierInstallPlan(string Model, long RuntimeBytes, long ModelBytes, string Text)
{
    public long TotalBytes => RuntimeBytes + ModelBytes;

    public const long RuntimeEstimateBytes = 3_000_000_000;

    public static BestTierInstallPlan Create(string model)
    {
        var modelBytes = model switch
        {
            BestTierGate.Seedvr2Large => 16_000_000_000L,
            BestTierGate.FlashVsr => 5_000_000_000L,
            _ => 7_000_000_000L,
        };
        var total = RuntimeEstimateBytes + modelBytes;
        var text = string.Create(CultureInfo.InvariantCulture,
            $"Best quality needs an extra download of about {total / 1_000_000_000.0:0} GB: the CUDA runtime and Python packages (about {RuntimeEstimateBytes / 1_000_000_000.0:0} GB) and the {model} model (about {modelBytes / 1_000_000_000.0:0} GB). Nothing is downloaded until you agree.");
        return new BestTierInstallPlan(model, RuntimeEstimateBytes, modelBytes, text);
    }
}

/// <summary>
/// Downloads and installs the add-on. Implemented later by the engine flavour downloader; the Host calls it only
/// after the user has agreed to the <see cref="BestTierInstallPlan"/>.
/// </summary>
public interface IBestTierInstaller
{
    Task InstallAsync(BestTierInstallPlan plan, bool userAgreed, IProgress<double>? progress, CancellationToken ct);
}
