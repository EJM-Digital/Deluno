using Deluno.Integrations.Metadata;
using Deluno.Media;

namespace Deluno.Persistence.Tests.Media;

/// <summary>
/// #861: a release filed under a region-specific alternate title ("Ne Zha 2"
/// listed as "哪吒之魔童闹海" on a tracker) has to be read back out of the
/// stored metadata blob before <c>ReleaseTitleMatch</c> can match it.
/// <see cref="CatalogueMetadata.ToUpdate"/> is the one place that writes
/// <see cref="MetadataSearchResult.AlternateTitles"/> into
/// <c>MetadataJson</c>; <see cref="CatalogueMetadata.ReadAlternateTitles"/> is
/// the one place that reads it back. This is the round trip between them.
/// </summary>
public sealed class CatalogueMetadataTests
{
    private static readonly MetadataSearchResult NeZha2 = new(
        "tmdb",
        "1011477",
        "movies",
        "Ne Zha 2",
        "哪吒之魔童闹海",
        2025,
        "overview",
        null,
        null,
        8.1,
        [],
        ["Animation"],
        null,
        null,
        AlternateTitles: ["哪吒之魔童闹海", "Nezha 2"]);

    [Fact]
    public void Alternate_titles_survive_the_round_trip_through_the_stored_metadata_blob()
    {
        var update = CatalogueMetadata.ToUpdate("entry-1", NeZha2, madeBy: null);

        var titles = CatalogueMetadata.ReadAlternateTitles(update.MetadataJson);

        Assert.NotNull(titles);
        Assert.Equal(["哪吒之魔童闹海", "Nezha 2"], titles);
    }

    [Fact]
    public void No_alternate_titles_reads_back_as_null_not_an_empty_list()
    {
        var withoutAlternates = NeZha2 with { AlternateTitles = null };
        var update = CatalogueMetadata.ToUpdate("entry-1", withoutAlternates, madeBy: null);

        Assert.Null(CatalogueMetadata.ReadAlternateTitles(update.MetadataJson));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    public void A_missing_or_malformed_blob_reads_back_as_null_rather_than_throwing(string? metadataJson)
        => Assert.Null(CatalogueMetadata.ReadAlternateTitles(metadataJson));
}
