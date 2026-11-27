namespace Portal.AiHost.Engine.Hardware;

/// <summary>A flavour that failed to load, tied to the engine pin and the driver it failed on.</summary>
public sealed record FlavourMark(string Flavour, string EngineTag, string Driver, string? Reason);

/// <summary>
/// Per-flavour "bad" marks keyed by (flavour, engine tag, driver version). A mark stops applying on its own when the
/// driver or the pinned engine changes (the key no longer matches); <see cref="Clear"/> is "Try again". In memory only;
/// <see cref="Snapshot"/> and the constructor let the caller persist and restore the list.
/// </summary>
public sealed class FlavourMarks
{
    private readonly object _sync = new();
    private readonly Dictionary<(string, string, string), FlavourMark> _marks = new();

    public FlavourMarks()
    {
    }

    public FlavourMarks(IEnumerable<FlavourMark> restored)
    {
        foreach (var m in restored)
        {
            _marks[Key(m.Flavour, m.EngineTag, m.Driver)] = m;
        }
    }

    public bool IsBad(string flavour, string? engineTag, string? driver)
    {
        lock (_sync)
        {
            return _marks.ContainsKey(Key(flavour, engineTag, driver));
        }
    }

    public void Mark(string flavour, string? engineTag, string? driver, string? reason = null)
    {
        lock (_sync)
        {
            _marks[Key(flavour, engineTag, driver)] = new FlavourMark(flavour, engineTag ?? "", driver ?? "", reason);
        }
    }

    /// <summary>Removes every mark of a flavour ("Try again"), or all marks when <paramref name="flavour"/> is null.</summary>
    public void Clear(string? flavour = null)
    {
        lock (_sync)
        {
            if (flavour == null)
            {
                _marks.Clear();
                return;
            }

            foreach (var k in _marks.Keys.Where(k => k.Item1 == flavour).ToList())
            {
                _marks.Remove(k);
            }
        }
    }

    public IReadOnlyList<FlavourMark> Snapshot()
    {
        lock (_sync)
        {
            return [.. _marks.Values];
        }
    }

    /// <summary>
    /// Builds the list from the earlier single flag: a true "Vulkan marked bad" becomes a Vulkan mark for the current
    /// engine tag and driver. The old value is only read, never rewritten.
    /// </summary>
    public static FlavourMarks FromLegacyVulkanBad(bool vulkanBad, string? engineTag, string? driver)
    {
        var m = new FlavourMarks();
        if (vulkanBad)
        {
            m.Mark(RuntimeProbe.Vulkan, engineTag, driver, "Carried over from the earlier setting.");
        }

        return m;
    }

    private static (string, string, string) Key(string flavour, string? tag, string? driver) =>
        (flavour, tag ?? "", driver ?? "");
}
