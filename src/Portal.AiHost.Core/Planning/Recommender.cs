using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Catalogue;

namespace Portal.AiHost.Core.Planning;

public sealed record Candidate(CatalogueEntry Entry, PlanResult Plan, bool Installed, bool FitsOnDisk, bool Recommendable, string Reason);

public sealed record Recommendation(Candidate? Best, IReadOnlyList<Candidate> Candidates);

/// <summary>What the recommender needs besides the catalogue and hardware.</summary>
public sealed record RecommendContext(
    IReadOnlySet<string> InstalledIds,
    long StoreFreeBytes,
    string StoreDriveName = "this drive",
    PlannerSettings? Settings = null,
    CommittedMemory? Committed = null);

/// <summary>
/// Pure recommender: for each catalogue entry of the wanted kind, plan it on this hardware, keep those that fit in
/// memory (with headroom), on disk, and run fast enough, then pick the highest quality (ties: faster).
/// Bigger hardware needs no code change: larger entries simply start to fit.
/// </summary>
public static class Recommender
{
    public const long DiskFixedHeadroom = 2L << 30;

    public static Recommendation Recommend(
        IEnumerable<CatalogueEntry> catalogue, string kind, HardwareProfile hw, MemorySnapshot mem, RecommendContext ctx)
    {
        var settings = ctx.Settings ?? new PlannerSettings();
        var budget = MemoryBudget.From(hw, Typical(hw, mem, settings), settings, ctx.Committed);
        var candidates = new List<Candidate>();
        foreach (var e in catalogue.Where(e => e.Kind == kind && CatalogueValidator.IsSupported(e)))
        {
            var plan = PlacementPlanner.Plan(e, budget, settings);
            var installed = ctx.InstalledIds.Contains(e.Id);
            var diskNeed = e.TotalBytes + (long)(e.TotalBytes * 0.02) + DiskFixedHeadroom;
            var onDisk = installed || ctx.StoreFreeBytes >= diskNeed;
            var fastEnough = kind != ModelKinds.Chat || plan.TokensPerSecond >= settings.MinTokensPerSecond;
            var reason = !plan.Fits ? plan.Reason
                : !fastEnough ? $"{e.DisplayName} would fit but run at about {plan.TokensPerSecond:0} words/s, too slow"
                : !onDisk ? $"{e.DisplayName} needs {diskNeed / 1e9:0} GB; {ctx.StoreDriveName} has {ctx.StoreFreeBytes / 1e9:0} GB free"
                : plan.Reason;
            candidates.Add(new Candidate(e, plan, installed, onDisk, plan.Fits && fastEnough && onDisk, reason));
        }

        var best = candidates
            .Where(c => c.Recommendable)
            .OrderByDescending(c => c.Entry.Quality)
            .ThenByDescending(c => c.Plan.TokensPerSecond)
            .ThenBy(c => c.Entry.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        return new Recommendation(best, candidates);
    }

    /// <summary>Live free RAM, capped at total minus the typical use by Windows and other apps.</summary>
    public static MemorySnapshot Typical(HardwareProfile hw, MemorySnapshot mem, PlannerSettings settings)
    {
        var reserve = Math.Max(2L << 30, (long)(hw.RamBytes * settings.TypicalOtherUseFraction));
        var available = mem.AvailableRamBytes > 0 ? mem.AvailableRamBytes : hw.RamBytes;
        return mem with { AvailableRamBytes = Math.Max(0, Math.Min(available, hw.RamBytes - reserve)) };
    }
}

public sealed record UpgradeOffer(
    string FromModelId, string ToModelId, string ToDisplayName, double ToQuality, long DownloadBytes, string Message);

public sealed record RecheckResult(
    bool FingerprintChanged, PlanResult? ActivePlan, UpgradeOffer? Upgrade, Recommendation Recommendation);

/// <summary>
/// "Re-check hardware": re-plans the active model (no download) and offers an upgrade when a better model now
/// fits. It never removes anything; the old model stays until the new one is verified and the user agrees.
/// </summary>
public static class HardwareRecheck
{
    public static RecheckResult Run(
        string? previousFingerprint,
        HardwareProfile hw,
        MemorySnapshot mem,
        IReadOnlyList<CatalogueEntry> catalogue,
        string kind,
        string? activeModelId,
        RecommendContext ctx)
    {
        var rec = Recommender.Recommend(catalogue, kind, hw, mem, ctx);
        var active = activeModelId is null ? null : catalogue.FirstOrDefault(e => e.Id == activeModelId);
        var activePlan = active is null ? null : PlacementPlanner.Plan(active, hw, mem, ctx.Settings, ctx.Committed);
        UpgradeOffer? offer = null;
        if (rec.Best is { } best && best.Entry.Id != activeModelId && (active is null || best.Entry.Quality > active.Quality))
        {
            var download = best.Installed ? 0 : best.Entry.TotalBytes;
            offer = new UpgradeOffer(activeModelId ?? "", best.Entry.Id, best.Entry.DisplayName, best.Entry.Quality, download,
                download > 0
                    ? $"Upgrade available: {best.Entry.DisplayName}, {download / 1e9:0} GB download"
                    : $"Upgrade available: {best.Entry.DisplayName} (already downloaded)");
        }

        return new RecheckResult(!string.Equals(previousFingerprint, hw.Fingerprint, StringComparison.Ordinal), activePlan, offer, rec);
    }
}

public sealed record UpgradeSwitch(string ActiveModelId, string KeptModelId);

/// <summary>
/// Guards the switch to an upgraded model: only once every file of the new model is stored and verified and the
/// new model has passed its test load and probe prompt. The old model is kept; removing it is a separate user action.
/// </summary>
public sealed class UpgradeFlow(IModelStore store)
{
    public async Task<bool> CanSwitchAsync(UpgradeOffer offer, bool probePassed, CancellationToken ct)
    {
        if (!probePassed)
        {
            return false;
        }

        var next = await store.GetAsync(offer.ToModelId, ct).ConfigureAwait(false);
        return next is { IsComplete: true } && next.Files.All(f => f.State != StoredFileState.Partial);
    }

    public async Task<UpgradeSwitch> SwitchAsync(UpgradeOffer offer, bool probePassed, CancellationToken ct)
    {
        if (!await CanSwitchAsync(offer, probePassed, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"'{offer.ToModelId}' is not verified and tested yet; '{offer.FromModelId}' stays active.");
        }

        return new UpgradeSwitch(offer.ToModelId, offer.FromModelId);
    }
}
