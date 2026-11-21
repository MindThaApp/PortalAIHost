using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Portal.AiHost.Contracts;

namespace Portal.AiHost.Core.Catalogue;

public enum CatalogueCheckStatus
{
    /// <summary>Signed by a trusted key, valid, and newer than the last accepted copy.</summary>
    Accepted,

    /// <summary>Same version and same bytes as the last accepted copy.</summary>
    Unchanged,

    /// <summary>Older version, or the same version with different content. Ignored.</summary>
    Rollback,

    /// <summary>No signature, or no trusted key verifies it. Ignored.</summary>
    BadSignature,

    /// <summary>Signed but not a valid catalogue. Ignored.</summary>
    Invalid,
}

public sealed record CatalogueCheckResult(
    CatalogueCheckStatus Status, ModelCatalogue? Catalogue, string ContentSha256, IReadOnlyList<string> Errors)
{
    public bool IsUsable => Status is CatalogueCheckStatus.Accepted or CatalogueCheckStatus.Unchanged;
}

/// <summary>
/// Public keys allowed to sign the catalogue: SubjectPublicKeyInfo (DER), base64. Two slots (current, next) so
/// the owner can roll keys without a gap. The private keys never live in the repo; see scripts/sign-catalogue.ps1.
/// Empty means nothing downloaded is trusted and only the packaged baseline is used (fail closed).
/// </summary>
public static class CatalogueTrust
{
    public static IReadOnlyList<string> PublicKeysSpkiBase64 { get; } = Array.Empty<string>();
}

/// <summary>
/// Verifies a detached ECDSA P-256 / SHA-256 signature over the exact catalogue bytes and applies rollback
/// protection: <c>catalogue_version</c> never goes backwards.
/// </summary>
public sealed class CatalogueVerifier
{
    private readonly IReadOnlyList<byte[]> _keys;

    public CatalogueVerifier(IEnumerable<string> trustedSpkiBase64)
    {
        _keys = trustedSpkiBase64.Select(Convert.FromBase64String).ToList();
    }

    /// <param name="json">Exact bytes of catalogue.json as fetched.</param>
    /// <param name="signatureFile">Contents of catalogue.json.sig: base64 of an IEEE P1363 (r||s) or DER signature.</param>
    /// <param name="lastAcceptedVersion">Highest version accepted so far (0 if none).</param>
    /// <param name="lastAcceptedSha256">Hex SHA-256 of the copy accepted at that version, if known.</param>
    public CatalogueCheckResult Check(
        byte[] json, string? signatureFile, long lastAcceptedVersion, string? lastAcceptedSha256, Version? hostVersion = null)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(json));
        if (!VerifySignature(json, signatureFile))
        {
            return new(CatalogueCheckStatus.BadSignature, null, hash, ["signature missing or not from a trusted key"]);
        }

        var (catalogue, errors) = CatalogueValidator.Parse(json, hostVersion);
        if (catalogue is null)
        {
            return new(CatalogueCheckStatus.Invalid, null, hash, errors);
        }

        if (catalogue.CatalogueVersion < lastAcceptedVersion)
        {
            return new(CatalogueCheckStatus.Rollback, null, hash,
                [$"version {catalogue.CatalogueVersion} is older than {lastAcceptedVersion}"]);
        }

        if (catalogue.CatalogueVersion == lastAcceptedVersion)
        {
            return string.Equals(hash, lastAcceptedSha256, StringComparison.OrdinalIgnoreCase)
                ? new(CatalogueCheckStatus.Unchanged, catalogue, hash, [])
                : new(CatalogueCheckStatus.Rollback, null, hash,
                    [$"version {catalogue.CatalogueVersion} was already accepted with different content"]);
        }

        return new(CatalogueCheckStatus.Accepted, catalogue, hash, []);
    }

    private bool VerifySignature(byte[] json, string? signatureFile)
    {
        if (string.IsNullOrWhiteSpace(signatureFile) || _keys.Count == 0)
        {
            return false;
        }

        byte[] sig;
        try
        {
            sig = Convert.FromBase64String(signatureFile.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        var format = sig.Length == 64 ? DSASignatureFormat.IeeeP1363FixedFieldConcatenation : DSASignatureFormat.Rfc3279DerSequence;
        foreach (var key in _keys)
        {
            try
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(key, out _);
                if (ecdsa.KeySize != 256)
                {
                    continue;
                }

                if (ecdsa.VerifyData(json, sig, HashAlgorithmName.SHA256, format))
                {
                    return true;
                }
            }
            catch (CryptographicException)
            {
                // A malformed key or signature never verifies.
            }
        }

        return false;
    }
}

/// <summary>Parses and checks a catalogue. Any structural error rejects the whole copy.</summary>
public static partial class CatalogueValidator
{
    public static (ModelCatalogue? Catalogue, IReadOnlyList<string> Errors) Parse(byte[] json, Version? hostVersion = null)
    {
        ModelCatalogue? catalogue;
        try
        {
            catalogue = JsonSerializer.Deserialize(json, CatalogueJsonContext.Default.ModelCatalogue);
        }
        catch (JsonException ex)
        {
            return (null, [$"not valid JSON: {ex.Message}"]);
        }

        if (catalogue is null)
        {
            return (null, ["empty catalogue"]);
        }

        var errors = Validate(catalogue);
        if (errors.Count > 0)
        {
            return (null, errors);
        }

        if (hostVersion is not null)
        {
            // Entries that need a newer host are hidden, not errors.
            catalogue.Models.RemoveAll(m => Version.TryParse(m.MinHostVersion, out var min) && min > hostVersion);
        }

        return (catalogue, errors);
    }

    public static string Serialize(ModelCatalogue catalogue) =>
        JsonSerializer.Serialize(catalogue, CatalogueJsonContext.Default.ModelCatalogue);

    public static List<string> Validate(ModelCatalogue c)
    {
        var errors = new List<string>();
        if (c.SchemaVersion != 1)
        {
            errors.Add($"unsupported schema_version {c.SchemaVersion}");
        }

        if (c.CatalogueVersion <= 0)
        {
            errors.Add("catalogue_version must be positive");
        }

        if (c.Models is null)
        {
            errors.Add("models missing");
            return errors;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in c.Models)
        {
            if (m is null)
            {
                errors.Add("null model entry");
                continue;
            }

            var id = m.Id ?? "";
            var p = $"model '{id}'";
            if (!IdPattern().IsMatch(id) || Store.OwnedFolder.IsReservedDeviceName(id))
            {
                errors.Add($"{p}: id must match {IdPattern()} and not be a reserved device name");
            }

            if (!ids.Add(id))
            {
                errors.Add($"{p}: duplicate id");
            }

            if (string.IsNullOrWhiteSpace(m.DisplayName) || m.Engine is null || m.Kind is null || m.Quant is null)
            {
                errors.Add($"{p}: display_name, engine, kind and quant are required");
            }

            if (m.Licence is null || string.IsNullOrWhiteSpace(m.Licence.Spdx)
                || m.Licence.Url is null || !m.Licence.Url.StartsWith("https://", StringComparison.Ordinal))
            {
                errors.Add($"{p}: licence spdx and https url required");
            }

            if (m.Features is null)
            {
                errors.Add($"{p}: features must not be null");
            }

            if (!RepoPattern().IsMatch(m.HfRepo ?? "") || (m.HfRepo ?? "").Contains("..", StringComparison.Ordinal))
            {
                errors.Add($"{p}: hf_repo must look like owner/name");
            }

            if (!CommitOrPlaceholderPattern().IsMatch(m.HfCommit ?? ""))
            {
                errors.Add($"{p}: hf_commit must be a 40-hex commit or a PLACEHOLDER- value");
            }

            if (m.ParamsTotalB <= 0 || m.ParamsActiveB <= 0 || m.ParamsActiveB > m.ParamsTotalB)
            {
                errors.Add($"{p}: params_total_b/params_active_b invalid");
            }

            if (m.ContextDefault <= 0 || m.ContextMax < m.ContextDefault)
            {
                errors.Add($"{p}: context_default must be positive and <= context_max");
            }

            if (!Version.TryParse(m.MinHostVersion, out _))
            {
                errors.Add($"{p}: min_host_version invalid");
            }

            var filesOk = ValidateFiles(m, p, errors);
            ValidateMemory(m, p, errors, filesOk);
        }

        return errors;
    }

    private static void ValidateMemory(CatalogueEntry m, string p, List<string> errors, bool filesOk)
    {
        var mem = m.Memory;
        if (mem is null)
        {
            errors.Add($"{p}: memory missing");
            return;
        }

        if (mem.WeightsBytes <= 0 || mem.NLayers <= 0 || mem.KvBytesPer1kCtx <= 0 || mem.ComputeOverheadBytes < 0)
        {
            errors.Add($"{p}: memory weights_bytes, n_layers and kv_bytes_per_1k_ctx must be positive");
            return;
        }

        if (mem.NonExpertBytes < 0 || mem.ExpertBytes < 0 || mem.NonExpertBytes + mem.ExpertBytes > mem.WeightsBytes)
        {
            errors.Add($"{p}: memory non_expert_bytes + expert_bytes must not exceed weights_bytes");
        }

        if (mem.ExpertBytes > 0 && m.ParamsActiveB >= m.ParamsTotalB)
        {
            errors.Add($"{p}: expert_bytes set on a dense model");
        }

        if (filesOk && mem.WeightsBytes > m.TotalBytes * 1.05)
        {
            errors.Add($"{p}: weights_bytes larger than the files");
        }
    }

    /// <summary>Returns true when the file list is structurally usable (no nulls).</summary>
    private static bool ValidateFiles(CatalogueEntry m, string p, List<string> errors)
    {
        if (m.Files is null || m.Files.Count == 0)
        {
            errors.Add($"{p}: no files");
            return false;
        }

        if (m.Files.Any(f => f is null || f.Name is null || f.Sha256 is null || f.Urls is null || f.Urls.Any(u => u is null)))
        {
            errors.Add($"{p}: a file entry is missing name, sha256 or urls");
            return false;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < m.Files.Count; i++)
        {
            var f = m.Files[i];
            if (!FileNamePattern().IsMatch(f.Name) || f.Name.Contains("..", StringComparison.Ordinal)
                || Store.OwnedFolder.IsReservedDeviceName(f.Name))
            {
                errors.Add($"{p}: unsafe file name '{f.Name}'");
            }

            if (!names.Add(f.Name))
            {
                errors.Add($"{p}: duplicate file '{f.Name}'");
            }

            if (f.Size <= 0)
            {
                errors.Add($"{p}: file '{f.Name}' size must be positive");
            }

            if (string.IsNullOrWhiteSpace(f.Sha256))
            {
                errors.Add($"{p}: file '{f.Name}' sha256 missing (use a placeholder if unknown)");
            }

            if (f.Urls.Count == 0 || f.Urls.Any(u => !Uri.TryCreate(u, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
            {
                errors.Add($"{p}: file '{f.Name}' needs https urls");
            }
            else if (!f.Urls[0].StartsWith($"https://huggingface.co/{m.HfRepo}/resolve/{m.HfCommit}/", StringComparison.Ordinal))
            {
                errors.Add($"{p}: first url of '{f.Name}' must resolve against hf_repo at hf_commit");
            }

            if (m.Files.Count > 1)
            {
                var split = SplitPattern().Match(f.Name);
                if (!split.Success || int.Parse(split.Groups[1].Value) != i + 1 || int.Parse(split.Groups[2].Value) != m.Files.Count)
                {
                    errors.Add($"{p}: split part '{f.Name}' must be -{i + 1:00000}-of-{m.Files.Count:00000}.gguf in order");
                }
            }
        }

        return true;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,95}/[A-Za-z0-9][A-Za-z0-9._-]{0,95}$")]
    private static partial Regex RepoPattern();

    [GeneratedRegex("^([0-9a-f]{40}|PLACEHOLDER-[A-Z-]+)$")]
    private static partial Regex CommitOrPlaceholderPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,199}\\.(gguf|bin)$")]
    private static partial Regex FileNamePattern();

    [GeneratedRegex("-(\\d{5})-of-(\\d{5})\\.gguf$")]
    private static partial Regex SplitPattern();

    /// <summary>Kinds and engines this host version can run; others are kept in the file but ignored.</summary>
    public static bool IsSupported(CatalogueEntry m) =>
        m.Engine == EngineKinds.LlamaServer && (m.Kind == ModelKinds.Chat || m.Kind == ModelKinds.Embedding);

    internal static string Sha256Hex(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}
