using System.Security.Cryptography;
using System.Text;
using Portal.AiHost.Core.Catalogue;

namespace Portal.AiHost.Core.Tests.Models;

public class CatalogueTests
{
    private static (ECDsa Key, string Spki) NewKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    private static string Sign(ECDsa key, byte[] data) =>
        Convert.ToBase64String(key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    private static byte[] WithVersion(long version) =>
        Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(TestData.SeedCatalogueBytes())
            .Replace("\"catalogue_version\": 2,", $"\"catalogue_version\": {version},", StringComparison.Ordinal));

    [Fact]
    public void SeedCatalogueIsValid()
    {
        var (catalogue, errors) = CatalogueValidator.Parse(TestData.SeedCatalogueBytes());
        Assert.Empty(errors);
        Assert.NotNull(catalogue);
        Assert.Contains(catalogue.Models, m => m.Kind == "chat");
        Assert.Contains(catalogue.Models, m => m.Kind == "embedding");
        Assert.True(catalogue.Models.Select(m => m.Maker).Distinct().Count() >= 6, "mixed-vendor defaults");
        Assert.Contains(catalogue.Models, m => m.Files.Count == 3 && m.TotalBytes > 100_000_000_000);
        Assert.All(catalogue.Models, m => Assert.True(m.Licence.Spdx is "Apache-2.0" or "MIT"));
    }

    [Fact]
    public void SeedEntriesArePinnedAndVerified()
    {
        var (catalogue, _) = CatalogueValidator.Parse(TestData.SeedCatalogueBytes());
        Assert.All(catalogue!.Models, m =>
        {
            Assert.True(m.IsDownloadable);
            Assert.All(m.Files, f => Assert.True(f.HasVerifiedHash));
        });
    }

    [Fact]
    public void SignedNewerCatalogueIsAccepted()
    {
        var (key, spki) = NewKey();
        using var _ = key;
        var json = WithVersion(5);
        var result = new CatalogueVerifier([spki]).Check(json, Sign(key, json), lastAcceptedVersion: 4, lastAcceptedSha256: null);
        Assert.Equal(CatalogueCheckStatus.Accepted, result.Status);
        Assert.Equal(5, result.Catalogue!.CatalogueVersion);
    }

    [Fact]
    public void DerSignatureAndNextKeyAreAccepted()
    {
        var (current, spkiCurrent) = NewKey();
        var (next, spkiNext) = NewKey();
        using var a = current;
        using var b = next;
        var json = WithVersion(2);
        var der = Convert.ToBase64String(next.SignData(json, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        Assert.Equal(CatalogueCheckStatus.Accepted, new CatalogueVerifier([spkiCurrent, spkiNext]).Check(json, der, 1, null).Status);
    }

    [Fact]
    public void TamperedUnsignedOrForeignSignedCopiesAreIgnored()
    {
        var (key, spki) = NewKey();
        var (other, _) = NewKey();
        using var a = key;
        using var b = other;
        var json = WithVersion(3);
        var sig = Sign(key, json);
        var verifier = new CatalogueVerifier([spki]);

        var tampered = (byte[])json.Clone();
        tampered[^5] ^= 1;
        Assert.Equal(CatalogueCheckStatus.BadSignature, verifier.Check(tampered, sig, 0, null).Status);
        Assert.Equal(CatalogueCheckStatus.BadSignature, verifier.Check(json, null, 0, null).Status);
        Assert.Equal(CatalogueCheckStatus.BadSignature, verifier.Check(json, "not base64!", 0, null).Status);
        Assert.Equal(CatalogueCheckStatus.BadSignature, verifier.Check(json, Sign(other, json), 0, null).Status);
        Assert.Equal(CatalogueCheckStatus.BadSignature, new CatalogueVerifier([]).Check(json, sig, 0, null).Status);
    }

    [Fact]
    public void VersionNeverGoesBackwards()
    {
        var (key, spki) = NewKey();
        using var _ = key;
        var verifier = new CatalogueVerifier([spki]);
        var v7 = WithVersion(7);
        var accepted = verifier.Check(v7, Sign(key, v7), 6, null);
        Assert.Equal(CatalogueCheckStatus.Accepted, accepted.Status);

        var v6 = WithVersion(6);
        Assert.Equal(CatalogueCheckStatus.Rollback, verifier.Check(v6, Sign(key, v6), 7, accepted.ContentSha256).Status);
        Assert.Equal(CatalogueCheckStatus.Unchanged, verifier.Check(v7, Sign(key, v7), 7, accepted.ContentSha256).Status);

        var v7Other = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(v7).Replace("2026-11-21", "2026-11-22", StringComparison.Ordinal));
        Assert.Equal(CatalogueCheckStatus.Rollback, verifier.Check(v7Other, Sign(key, v7Other), 7, accepted.ContentSha256).Status);
    }

    [Fact]
    public void SignedButInvalidCatalogueIsRejected()
    {
        var (key, spki) = NewKey();
        using var _ = key;
        var bad = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(WithVersion(9))
            .Replace("\"name\": \"Qwen3-8B-Q4_K_M.gguf\"", "\"name\": \"..\\\\..\\\\evil.gguf\"", StringComparison.Ordinal));
        var result = new CatalogueVerifier([spki]).Check(bad, Sign(key, bad), 1, null);
        Assert.Equal(CatalogueCheckStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, e => e.Contains("unsafe file name", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatorChecksUrlsSplitNamesAndMemory()
    {
        var (catalogue, _) = CatalogueValidator.Parse(TestData.SeedCatalogueBytes());
        var entry = TestData.Entry("x", ("x.gguf", new byte[10]));
        var c = new ModelCatalogue { CatalogueVersion = 1, Models = { entry } };
        Assert.Empty(CatalogueValidator.Validate(c));

        entry.Files[0].Urls[0] = "http://huggingface.co/test/repo/resolve/" + TestData.Commit + "/x.gguf";
        Assert.NotEmpty(CatalogueValidator.Validate(c));
        entry.Files[0].Urls[0] = "https://huggingface.co/test/repo/resolve/main/x.gguf";
        Assert.Contains(CatalogueValidator.Validate(c), e => e.Contains("hf_commit", StringComparison.Ordinal));

        var split = TestData.Entry("s", ("s-00002-of-00002.gguf", new byte[5]), ("s-00001-of-00002.gguf", new byte[5]));
        Assert.Contains(CatalogueValidator.Validate(new ModelCatalogue { CatalogueVersion = 1, Models = { split } }),
            e => e.Contains("split part", StringComparison.Ordinal));
        Assert.NotNull(catalogue);
    }

    [Fact]
    public void EntriesNeedingNewerHostAreHidden()
    {
        var json = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(TestData.SeedCatalogueBytes())
            .Replace("\"min_host_version\": \"1.0.0\"", "\"min_host_version\": \"9.0.0\"", StringComparison.Ordinal));
        var (catalogue, errors) = CatalogueValidator.Parse(json, new Version(1, 0, 0));
        Assert.Empty(errors);
        Assert.Empty(catalogue!.Models);
    }

    [Fact]
    public void AdviceIsOptionalAndParsesWhenPresent()
    {
        var (plain, e1) = CatalogueValidator.Parse(TestData.SeedCatalogueBytes());
        Assert.Empty(e1);
        Assert.All(plain!.Models, m => Assert.True(m.Advice is null || m.Advice.GoodFor is not null));

        var text = Encoding.UTF8.GetString(TestData.SeedCatalogueBytes());
        var first = text.IndexOf("\"models\"", StringComparison.Ordinal);
        var brace = text.IndexOf('{', text.IndexOf('[', first));
        var withAdvice = text.Insert(brace + 1,
            "\"advice\": {\"summary\": \"Good all-rounder\", \"good_for\": [\"chat\"], \"caveats\": [\"slow without a graphics card\"], \"hardware\": \"8 GB laptop\"},");
        var (cat, errors) = CatalogueValidator.Parse(Encoding.UTF8.GetBytes(withAdvice));
        Assert.Empty(errors);
        var advice = cat!.Models[0].Advice;
        Assert.NotNull(advice);
        Assert.Equal("Good all-rounder", advice.Summary);
        Assert.Equal(["chat"], advice.GoodFor);
        Assert.Equal("8 GB laptop", advice.Hardware);
    }
}
