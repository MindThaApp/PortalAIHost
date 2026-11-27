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

            var verifier = new CatalogueVerifier(trustedKeys);
            var result = verifier.Check(File.ReadAllBytes(json), File.ReadAllText(json + ".sig"), 0, null, HostVersion());
            if (!result.IsUsable || result.Catalogue is null)
            {
                return null;
            }

            return WithGenerationList(result.Catalogue.Models, Path.Combine(baseDirectory, "catalogue", GenerationFileName), verifier);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    /// <summary>The image and video list, signed on its own so a problem with it never costs the main list.</summary>
    public const string GenerationFileName = "catalogue-gen.json";

    /// <summary>
    /// Adds the entries of catalogue-gen.json (own signature) to the main list. A missing, unreadable, badly signed or
    /// invalid file adds nothing; the main list is returned as it is. Entries whose id the main list already has are skipped.
    /// </summary>
    private static IReadOnlyList<CatalogueEntry> WithGenerationList(List<CatalogueEntry> main, string path, CatalogueVerifier verifier)
    {
        try
        {
            if (!File.Exists(path) || !File.Exists(path + ".sig"))
            {
                return main;
            }

            var gen = verifier.Check(File.ReadAllBytes(path), File.ReadAllText(path + ".sig"), 0, null, HostVersion());
            if (!gen.IsUsable || gen.Catalogue is null)
            {
                return main;
            }

            var ids = main.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            return [.. main, .. gen.Catalogue.Models.Where(e => ids.Add(e.Id))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return main;
        }
    }

    private static Version HostVersion() => typeof(CatalogueSource).Assembly.GetName().Version ?? new Version(1, 0, 0);
}
