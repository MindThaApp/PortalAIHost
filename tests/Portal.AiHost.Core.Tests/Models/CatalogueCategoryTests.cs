using System.Text;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Catalogue;

namespace Portal.AiHost.Core.Tests.Models;

public class CatalogueCategoryTests
{
    private static ModelCatalogue Shipped() => CatalogueValidator.Parse(TestData.SeedCatalogueBytes()).Catalogue!;

    private static ModelCatalogue ParseWith(Func<string, string> edit)
    {
        var json = edit(Encoding.UTF8.GetString(TestData.SeedCatalogueBytes()));
        var (catalogue, errors) = CatalogueValidator.Parse(Encoding.UTF8.GetBytes(json));
        Assert.Empty(errors);
        return catalogue!;
    }

    [Fact]
    public void Every_shipped_entry_names_a_known_category()
    {
        Assert.All(Shipped().Models, m =>
        {
            Assert.False(string.IsNullOrEmpty(m.Category), m.Id);
            Assert.Contains(m.Category!, ModelCategories.Ordered);
        });
    }

    [Fact]
    public void Category_is_read_from_the_entry()
    {
        var c = Shipped();
        Assert.Equal(ModelCategories.Search, c.Models.Single(m => m.Id == "nomic-embed-text-v1.5-q8").EffectiveCategory);
        Assert.Equal(ModelCategories.Reasoning, c.Models.Single(m => m.Id == "gpt-oss-20b").EffectiveCategory);
        Assert.Equal(ModelCategories.Video, c.Models.Single(m => m.Id == "wan2.1-t2v-1.3b").EffectiveCategory);
    }

    [Fact]
    public void An_entry_without_a_category_falls_back_to_its_kind()
    {
        var c = ParseWith(s => s.Replace("\"category\": \"search\",", "", StringComparison.Ordinal)
                                .Replace("\"category\": \"chat\",", "", StringComparison.Ordinal)
                                .Replace("\"category\": \"video\",", "", StringComparison.Ordinal));
        Assert.Null(c.Models.First(m => m.Kind == "chat" && m.Category is null).Category);
        Assert.Equal(ModelCategories.Chat, c.Models.First(m => m.Id == "qwen3-8b-q4km").EffectiveCategory);
        Assert.Equal(ModelCategories.Search, c.Models.First(m => m.Kind == "embedding").EffectiveCategory);
        Assert.Equal(ModelCategories.Video, c.Models.First(m => m.Kind == "video").EffectiveCategory);
    }

    [Fact]
    public void An_unknown_category_falls_back_to_the_kind_and_does_not_reject_the_list()
    {
        var c = ParseWith(s => s.Replace("\"category\": \"search\",", "\"category\": \"time-travel\",", StringComparison.Ordinal));
        Assert.Equal(ModelCategories.Search, c.Models.First(m => m.Kind == "embedding").EffectiveCategory);
    }

    [Fact]
    public void Kinds_map_to_categories()
    {
        Assert.Equal(ModelCategories.Chat, ModelCategories.FromKind("chat"));
        Assert.Equal(ModelCategories.Search, ModelCategories.FromKind("embedding"));
        Assert.Equal(ModelCategories.Speech, ModelCategories.FromKind("transcription"));
        Assert.Equal(ModelCategories.Video, ModelCategories.FromKind("video"));
        Assert.Equal(ModelCategories.Other, ModelCategories.FromKind("something-new"));
        Assert.Equal(ModelCategories.Coding, ModelCategories.Resolve("CODING", "chat"));
    }

    [Fact]
    public void Video_and_image_entries_are_listed_but_never_downloadable()
    {
        var media = Shipped().Models.Where(m => m.Kind is "video" or "image").ToList();
        Assert.Equal(["sd-1.5", "wan2.1-t2v-1.3b"], media.Select(m => m.Id).Order(StringComparer.Ordinal));
        Assert.All(media, m =>
        {
            Assert.False(m.Available, m.Id);
            Assert.False(m.IsDownloadable, m.Id);
            Assert.False(CatalogueValidator.IsSupported(m), m.Id);
            Assert.NotNull(m.MinVramGb);
            Assert.True(m.RecommendedVramGb >= m.MinVramGb);
            Assert.All(m.Files, f => Assert.True(f.HasVerifiedHash, m.Id + "/" + f.Name));
            Assert.Equal(ModelCategories.FromKind(m.Kind), m.EffectiveCategory);
        });
    }

    [Fact]
    public void The_video_model_has_three_pinned_parts_with_roles_from_two_repos()
    {
        var wan = Shipped().Models.Single(m => m.Id == "wan2.1-t2v-1.3b");
        Assert.Equal(["diffusion_model", "vae", "text_encoder"], wan.Files.Select(f => f.Role));
        Assert.Equal(6_747_264_190, wan.TotalBytes);
        Assert.Equal("city96/umt5-xxl-encoder-gguf", wan.Files[2].HfRepo);
        Assert.All(wan.Files, f => Assert.True(f.HasPinnedSource));
        Assert.Equal(4, Shipped().Models.Single(m => m.Id == "sd-1.5").MinVramGb);
    }

    [Fact]
    public void A_file_from_another_repo_needs_a_pinned_commit_and_a_matching_url()
    {
        var json = Encoding.UTF8.GetString(TestData.SeedCatalogueBytes())
            .Replace("b535255bee98c2b0a59ea7c0ae2dcd0c6657b3b7\",", "PLACEHOLDER-NOPE\",", StringComparison.Ordinal);
        var (catalogue, errors) = CatalogueValidator.Parse(Encoding.UTF8.GetBytes(json));
        Assert.Null(catalogue);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Available_false_blocks_download_even_with_verified_files()
    {
        var c = ParseWith(s => s.Replace("\"min_host_version\": \"1.0.0\",\n      \"notes\": \"Commit, sizes and SHA-256 pinned from the Hugging Face file listing. Layer and KV numbers from the model config (unverified). Non-thinking.\"",
            "\"min_host_version\": \"1.0.0\", \"available\": false,\n      \"notes\": \"x\"", StringComparison.Ordinal));
        var m = c.Models.Single(e => e.Id == "qwen3-30b-a3b-instruct-2507-q4km");
        Assert.False(m.Available);
        Assert.False(m.IsDownloadable);
        Assert.True(c.Models.Single(e => e.Id == "qwen3-8b-q4km").IsDownloadable);
    }

    [Fact]
    public void Available_defaults_to_true_and_the_field_round_trips()
    {
        var c = Shipped();
        Assert.True(c.Models.Single(m => m.Id == "qwen3-8b-q4km").Available);
        var again = CatalogueValidator.Parse(Encoding.UTF8.GetBytes(CatalogueValidator.Serialize(c))).Catalogue!;
        Assert.Equal(c.Models.Count, again.Models.Count);
        Assert.Equal("video", again.Models.Single(m => m.Id == "wan2.1-t2v-1.3b").Category);
        Assert.False(again.Models.Single(m => m.Id == "wan2.1-t2v-1.3b").Available);
        Assert.Equal(8, again.Models.Single(m => m.Id == "wan2.1-t2v-1.3b").MinVramGb);
        Assert.Equal("text_encoder", again.Models.Single(m => m.Id == "wan2.1-t2v-1.3b").Files[2].Role);
    }
}
