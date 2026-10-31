using System.Text.Json;
using Deluno.Integrations.Metadata;
using Deluno.Integrations.Search.Trackers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Integrations.Tests.Search.Trackers;

/// <summary>Small rules of the tracker and metadata code that the larger tests walk past. #1268.</summary>
public sealed class TrackerSmallPiecesTests
{
    private static readonly TrackerDefinitionCatalog Catalog = new(overrideDirectory: null, NullLogger<TrackerDefinitionCatalog>.Instance);

    [Fact]
    public void A_text_that_cannot_be_mended_is_left_as_it_is()
    {
        // A letter outside Windows-1252 cannot have been a mis-read byte.
        Assert.Equal("ÃĀ", ReleaseNameText.RepairMojibake("ÃĀ"));
        // Decodes to something shorter, but only a space, not a letter.
        Assert.Equal("Â ", ReleaseNameText.RepairMojibake("Â "));
        // Not valid UTF-8 once the bytes are put back together.
        Assert.Equal("Ã(", ReleaseNameText.RepairMojibake("Ã("));
    }

    [Fact]
    public void Category_numbers_are_matched_by_family_description_and_id()
    {
        var definition = Catalog.Find(Catalog.List().First().Id)!;
        var mapping = definition.Caps.CategoryMappings.First(m => TrackerCategories.NumberOf(m.Category) is not null);
        var number = TrackerCategories.NumberOf(mapping.Category)!.Value;

        Assert.Contains(mapping.SiteId, TrackerCategories.SiteCategoriesFor(definition, [number]));
        Assert.Contains(mapping.SiteId, TrackerCategories.SiteCategoriesFor(definition, [number / 1000 * 1000]));
        Assert.Equal(
            TrackerCategories.SiteCategoriesFor(definition, [2000, 5000]),
            TrackerCategories.SiteCategoriesFor(definition, []));
        Assert.Contains(number, TrackerCategories.NumbersFor(definition, mapping.SiteId));
        Assert.Contains(number, TrackerCategories.NumbersFor(definition, mapping.Description, byDescription: true));
        Assert.Empty(TrackerCategories.NumbersFor(definition, "  "));
        Assert.Empty(TrackerCategories.NumbersFor(definition, null));
        Assert.Empty(TrackerCategories.NumbersFor(definition, "no-such-category-id"));
        Assert.True(TrackerCategories.Covers(2040, 2040));
        Assert.True(TrackerCategories.Covers(2000, 2040));
        Assert.False(TrackerCategories.Covers(2040, 2000));
        Assert.False(TrackerCategories.Covers(2000, 5040));
        Assert.Equal([2000, 5000], TrackerCategories.Parse("2000, 5000;x"));
        Assert.Empty(TrackerCategories.Parse(null));
        Assert.Null(TrackerCategories.NumberOf("Not A Category"));
    }

    [Fact]
    public void A_search_asks_only_for_the_family_of_what_it_searches()
    {
        Assert.Equal([2040], TrackerCategories.ForFamily(false, [2040, 5030], null));
        Assert.Equal([5030], TrackerCategories.ForFamily(true, [5030, 2040], "2000"));
        Assert.Equal([2010], TrackerCategories.ForFamily(false, [5030], "2010,5000"));
        Assert.Equal([2000], TrackerCategories.ForFamily(false, null, null));
        Assert.Equal([5000], TrackerCategories.ForFamily(true, [], "2000"));
    }

    [Fact]
    public void A_tracker_definition_is_named_and_given_a_privacy_from_the_catalogue()
    {
        var names = new TrackerDefinitionNames(Catalog);
        var id = Catalog.List().First().Id;

        Assert.False(string.IsNullOrWhiteSpace(names.DisplayName(id)));
        Assert.NotNull(names.Privacy(id));
        Assert.Null(names.DisplayName("no-such-tracker"));
        Assert.Null(names.Privacy("no-such-tracker"));
    }

    [Fact]
    public void Artwork_is_found_in_the_cache_only_by_a_full_hex_key_under_the_local_path()
    {
        var key = new string('a', 32) + new string('F', 32);

        Assert.True(ArtworkCacheKeys.TryGet($" /api/metadata/artwork/{key}/ ", out var found));
        Assert.Equal(key.ToLowerInvariant(), found);
        Assert.False(ArtworkCacheKeys.TryGet(null, out var none));
        Assert.Equal(string.Empty, none);
        Assert.False(ArtworkCacheKeys.TryGet("  ", out _));
        Assert.False(ArtworkCacheKeys.TryGet($"https://elsewhere.example/api/metadata/artwork/{key}", out _));
        Assert.False(ArtworkCacheKeys.TryGet("/api/metadata/artwork/abc", out _));
        Assert.False(ArtworkCacheKeys.TryGet("/api/metadata/artwork/" + new string('g', 64), out _));
        Assert.True(ArtworkCacheKeys.TryGet("/api/metadata/artwork/" + new string('0', 10) + new string('9', 54), out _));
    }

    [Theory]
    [InlineData("tmdb", "omdb", true)]
    [InlineData(" TVDB ", "OMDB", true)]
    [InlineData("tmdb", "tvdb", false)]
    [InlineData("omdb", "omdb", false)]
    [InlineData(null, "omdb", false)]
    public void A_weak_match_never_replaces_a_link_to_a_strong_provider(string? stored, string matchProvider, bool downgrades)
    {
        var match = new MetadataSearchResult(matchProvider, "1", "movie", "Dune", null, 2021, null, null, null, null, [], [], null, null);

        Assert.Equal(downgrades, MetadataLinkPolicy.WouldDowngradeLink(stored, match));
    }

    [Fact]
    public void An_identifier_is_read_from_a_string_or_a_number_and_nothing_else()
    {
        var options = new JsonSerializerOptions { Converters = { new NumberOrStringConverter() } };

        Assert.Equal("abc", JsonSerializer.Deserialize<string>("\"abc\"", options));
        Assert.Equal("123", JsonSerializer.Deserialize<string>("123", options));
        Assert.Equal("1.5", JsonSerializer.Deserialize<string>("1.5", options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<string>("true", options));
        Assert.Equal("\"abc\"", JsonSerializer.Serialize<string?>("abc", options));
    }
}
