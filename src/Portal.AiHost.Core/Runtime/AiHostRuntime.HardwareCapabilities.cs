using Portal.AiHost.Contracts;

namespace Portal.AiHost.Core.Runtime;

public sealed partial class AiHostRuntime
{
    /// <summary>Set by the host once the hardware probe has run; null until then (the block is left out).</summary>
    public Func<HardwareCapabilities?>? HardwareInfo { get; set; }

    /// <summary>The hardware block; a paired device gets it without the driver strings.</summary>
    private HardwareCapabilities? BuildHardwareCapabilities(bool paired)
    {
        HardwareCapabilities? hw;
        try
        {
            hw = HardwareInfo?.Invoke();
        }
        catch (Exception)
        {
            return null;
        }

        if (hw is null || !paired)
        {
            return hw;
        }

        return new HardwareCapabilities
        {
            Gpus = hw.Gpus.Select(g => new HardwareGpu { Name = g.Name, Vendor = g.Vendor, VramBytes = g.VramBytes }).ToList(),
            Backends = hw.Backends,
            AvailableFlavours = hw.AvailableFlavours,
            DownloadableFlavours = hw.DownloadableFlavours,
        };
    }
}
