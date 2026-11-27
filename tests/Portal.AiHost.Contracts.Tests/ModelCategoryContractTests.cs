using System.Text.Json;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;

namespace Portal.AiHost.Contracts.Tests;

public class ModelCategoryContractTests
{
    private static readonly AiHostJsonContext Ctx = AiHostJsonContext.Default;

    [Fact]
    public void A_listing_from_an_older_host_has_no_category_and_is_not_coming_soon()
    {
        var listing = JsonSerializer.Deserialize("""
            { "models": [ { "id": "a", "display_name": "A", "kind": "chat", "engine": "llama-server", "size_bytes": 5, "quality": 1 } ] }
            """, Ctx.ModelListing)!;
        var m = listing.Models.Single();
        Assert.Null(m.Category);
        Assert.False(m.ComingSoon);
        Assert.Null(m.MinVramGb);
        Assert.Equal(ModelCategories.Chat, ModelCategories.Resolve(m.Category, m.Kind));
    }

    [Fact]
    public void Category_coming_soon_and_vram_round_trip()
    {
        var json = JsonSerializer.Serialize(new CatalogueModelSummary
        {
            Id = "v", DisplayName = "V", Kind = "video", Category = ModelCategories.Video, ComingSoon = true,
            MinVramGb = 8, RecommendedVramGb = 12,
        }, Ctx.CatalogueModelSummary);
        Assert.Contains("\"category\":\"video\"", json.Replace(" ", ""));
        var back = JsonSerializer.Deserialize(json, Ctx.CatalogueModelSummary)!;
        Assert.Equal("video", back.Category);
        Assert.True(back.ComingSoon);
        Assert.Equal(8, back.MinVramGb);
        Assert.Equal(12, back.RecommendedVramGb);
    }

    [Fact]
    public void Unknown_categories_fall_back_to_the_kind()
    {
        Assert.Equal(ModelCategories.Search, ModelCategories.Resolve("holograms", ModelKinds.Embedding));
        Assert.Equal(ModelCategories.Speech, ModelCategories.Resolve(null, ModelKinds.Transcription));
        Assert.Equal(ModelCategories.Other, ModelCategories.Resolve("", "mystery"));
    }
}
