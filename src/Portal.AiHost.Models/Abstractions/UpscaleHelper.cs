namespace Portal.AiHost.Core.Abstractions;

/// <summary>The upscale helper as the job code sees it (the real client wraps portal-upscale.exe).</summary>
public interface IUpscaleHelper
{
    /// <summary>True while the helper program is installed.</summary>
    bool EnginePresent { get; }

    /// <summary>
    /// Upscales the picture at <paramref name="input"/> and writes a NEW file at <paramref name="outputPath"/> (it never
    /// replaces a file). <paramref name="device"/> is "cpu" or "dml". <paramref name="progress"/> reports 0 to 1.
    /// </summary>
    Task UpscaleAsync(string modelPath, string input, string outputPath, int scale, string device,
        Action<double> progress, CancellationToken ct);
}
