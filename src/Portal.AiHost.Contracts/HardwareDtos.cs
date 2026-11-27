namespace Portal.AiHost.Contracts;

/// <summary>The <c>hardware</c> block of /host/capabilities (docs/hardware-backends-and-upscaling.md 5.3).</summary>
public sealed class HardwareCapabilities
{
    public List<HardwareGpu> Gpus { get; set; } = new();

    /// <summary>Flavour in use per engine: <c>chat</c>, <c>generation</c>, <c>upscale</c> (for example <c>vulkan</c>).</summary>
    public Dictionary<string, string> Backends { get; set; } = new();

    /// <summary>Engine flavours installed on this PC.</summary>
    public List<string> AvailableFlavours { get; set; } = new();

    /// <summary>Flavours this PC could use that are not installed yet.</summary>
    public List<string> DownloadableFlavours { get; set; } = new();
}

public sealed class HardwareGpu
{
    public string Name { get; set; } = "";
    public string Vendor { get; set; } = "";
    public long VramBytes { get; set; }

    /// <summary>Driver version. Left out for paired devices.</summary>
    public string? Driver { get; set; }
}
