using Portal.AiHost.Core.Catalogue;

namespace PortalAIHost.Service;

/// <summary>
/// Loads the model list the host may offer. Fail closed: only a catalogue signed by a trusted key is used, and with
/// no trusted key configured there is no list at all (the window shows "model list unavailable").
/// </summary>
internal static class CatalogueSource
{
    /// <summary>
    /// Development builds only: full path of an unsigned catalogue.json to load for testing. Ignored in Release
    /// builds, where the code that reads it is not compiled in.
    /// </summary>
    public const string DevUnsignedVariable = "PORTALAIHOST_DEV_UNSIGNED_CATALOGUE";

    public static IReadOnlyList<CatalogueEntry>? Load(string baseDirectory, Func<string, string?> env) =>
        Load(baseDirectory, env, CatalogueTrust.PublicKeysSpkiBase64);

    internal static IReadOnlyList<CatalogueEntry>? Load(string baseDirectory, Func<string, string?> env, IReadOnlyList<string> trustedKeys)
    {
#if DEBUG
        if (env(DevUnsignedVariable) is { Length: > 0 } devPath && Path.IsPathFullyQualified(devPath))
        {
            try
            {
                var (dev, _) = CatalogueValidator.Parse(File.ReadAllBytes(devPath), HostVersion());
                return dev?.Models;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
#else
        _ = env;
#endif
        if (trustedKeys.Count == 0)
        {
            return null;
        }

        var json = Path.Combine(baseDirectory, "catalogue", "catalogue.json");
        try
        {
            if (!File.Exists(json) || !File.Exists(json + ".sig"))
            {
                return null;
            }

            var result = new CatalogueVerifier(trustedKeys)
                .Check(File.ReadAllBytes(json), File.ReadAllText(json + ".sig"), 0, null, HostVersion());
            return result.IsUsable ? result.Catalogue?.Models : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    private static Version HostVersion() => typeof(CatalogueSource).Assembly.GetName().Version ?? new Version(1, 0, 0);
}
