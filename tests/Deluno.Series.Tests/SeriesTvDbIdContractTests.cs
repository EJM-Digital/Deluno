using System.Text.Json;
using Deluno.Series.Contracts;

namespace Deluno.Series.Tests;

public sealed class SeriesTvDbIdContractTests
{
    private static SeriesListItem Series(string? metadataJson)
    {
        var now = DateTimeOffset.Parse("2026-09-29T00:00:00Z");
        return new SeriesListItem(
            "s1", "Lioness", 2023, null, true, false,
            "tmdb", "1", null, null, null, null, null, [], null, null, metadataJson, null, now, now);
    }

    [Fact]
    public void The_series_API_carries_the_TheTVDB_id_the_show_page_shows()
    {
        var item = Series("""{"TvDbId":"388589"}""");
        Assert.Equal("388589", item.TvDbId);

        var json = JsonSerializer.Serialize(item, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        Assert.Equal("388589", document.RootElement.GetProperty("tvDbId").GetString());
    }

    [Fact]
    public void A_show_without_a_TheTVDB_id_reports_none()
    {
        Assert.Null(Series(null).TvDbId);
        Assert.Null(Series("""{"Title":"x"}""").TvDbId);
    }
}
