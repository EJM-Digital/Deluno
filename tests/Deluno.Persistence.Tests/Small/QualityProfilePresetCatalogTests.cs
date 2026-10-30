using Deluno.Quality.Presets;

namespace Deluno.Persistence.Tests.Small;

/// <summary>#1268: the quality profile presets the setup guide offers.</summary>
public sealed class QualityProfilePresetCatalogTests
{
    [Fact]
    public void The_catalogue_offers_four_film_and_three_tv_presets_with_unique_ids()
    {
        Assert.Equal(7, QualityProfilePresetCatalog.All.Count);
        Assert.Equal(4, QualityProfilePresetCatalog.All.Count(p => p.MediaType == "movies"));
        Assert.Equal(3, QualityProfilePresetCatalog.All.Count(p => p.MediaType == "tv"));
        Assert.Equal(7, QualityProfilePresetCatalog.All.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public void Every_preset_allows_the_quality_it_stops_upgrading_at()
    {
        foreach (var preset in QualityProfilePresetCatalog.All)
        {
            var allowed = preset.AllowedQualities.Split(',');
            Assert.Contains(preset.CutoffQuality, allowed);
            Assert.False(string.IsNullOrWhiteSpace(preset.Name));
            Assert.False(string.IsNullOrWhiteSpace(preset.Description));
            Assert.Equal(1, preset.Version);
        }
    }

    [Fact]
    public void The_any_quality_preset_never_upgrades_and_the_standard_ones_do_where_stated()
    {
        var any = QualityProfilePresetCatalog.FindById("any-movies")!;
        Assert.False(any.UpgradeUntilCutoff);
        Assert.False(any.UpgradeUnknownItems);
        Assert.False(QualityProfilePresetCatalog.FindById("standard-tv")!.UpgradeUntilCutoff);
        Assert.True(QualityProfilePresetCatalog.FindById("hd-tv")!.UpgradeUntilCutoff);
        Assert.Equal("Remux 1080p", QualityProfilePresetCatalog.FindById("premium-movies")!.CutoffQuality);
        Assert.Equal("WEB 2160p", QualityProfilePresetCatalog.FindById("4k-movies")!.CutoffQuality);
        Assert.Equal("WEB 2160p", QualityProfilePresetCatalog.FindById("4k-tv")!.CutoffQuality);
        Assert.Equal("WEB 1080p", QualityProfilePresetCatalog.FindById("standard-movies")!.CutoffQuality);
    }

    [Fact]
    public void A_preset_is_found_by_id_ignoring_case_and_an_unknown_id_finds_none()
    {
        Assert.Equal("hd-tv", QualityProfilePresetCatalog.FindById("HD-TV")!.Id);
        Assert.Null(QualityProfilePresetCatalog.FindById("nope"));
    }
}
