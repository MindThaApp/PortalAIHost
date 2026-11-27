// Frozen copy of the catalogue models and validator of host 1.0.0 (commit ab65839), as shipped to the Store. The published
// catalogue.json must keep passing this, so a host that has not been updated never refuses the whole model list.
// Do not "fix" or modernise this file.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Portal.AiHost.Contracts;

namespace Portal.AiHost.Core.Tests.LegacyHost;

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
            if (!IdPattern().IsMatch(id) || Portal.AiHost.Core.Store.OwnedFolder.IsReservedDeviceName(id))
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
                || Portal.AiHost.Core.Store.OwnedFolder.IsReservedDeviceName(f.Name))
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
