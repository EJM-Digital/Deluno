using Deluno.Platform.Quality;

namespace Deluno.Platform.Tests.Quality;

public sealed class LibraryQualityDeciderTests
{
    [Fact]
    public void Decide_marks_item_missing_when_no_file_exists()
    {
        var decision = LibraryQualityDecider.Decide(
            mediaLabel: "movie",
            hasFile: false,
            currentQuality: null,
            cutoffQuality: "WEB 1080p",
            upgradeUntilCutoff: true,
            upgradeUnknownItems: true);

        Assert.Equal("missing", decision.WantedStatus);
        Assert.False(decision.QualityCutoffMet);
        Assert.Null(decision.CurrentQuality);
        Assert.Equal("WEB 1080p", decision.TargetQuality);
        Assert.Contains("still looking", decision.WantedReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decide_marks_cutoff_met_when_current_quality_is_at_or_above_target()
    {
        var decision = LibraryQualityDecider.Decide(
            mediaLabel: "episode",
            hasFile: true,
            currentQuality: "Bluray 2160p",
            cutoffQuality: "WEB 1080p",
            upgradeUntilCutoff: true,
            upgradeUnknownItems: true);

        Assert.Equal("waiting", decision.WantedStatus);
        Assert.True(decision.QualityCutoffMet);
        Assert.Equal("Bluray 2160p", decision.CurrentQuality);
        Assert.Equal("WEB 1080p", decision.TargetQuality);
    }

    [Fact]
    public void Decide_marks_upgrade_when_current_quality_is_below_cutoff_and_upgrades_are_enabled()
    {
        var decision = LibraryQualityDecider.Decide(
            mediaLabel: "movie",
            hasFile: true,
            currentQuality: "WEB-DL 720p",
            cutoffQuality: "WEB 1080p",
            upgradeUntilCutoff: true,
            upgradeUnknownItems: true);

        Assert.Equal("upgrade", decision.WantedStatus);
        Assert.False(decision.QualityCutoffMet);
        Assert.Equal("WEB 720p", decision.CurrentQuality);
        Assert.Equal("WEB 1080p", decision.TargetQuality);
    }

    [Fact]
    public void Decide_does_not_upgrade_unknown_quality_unless_enabled()
    {
        var held = LibraryQualityDecider.Decide(
            mediaLabel: "movie",
            hasFile: true,
            currentQuality: null,
            cutoffQuality: "WEB 1080p",
            upgradeUntilCutoff: true,
            upgradeUnknownItems: false);

        var upgraded = LibraryQualityDecider.Decide(
            mediaLabel: "movie",
            hasFile: true,
            currentQuality: null,
            cutoffQuality: "WEB 1080p",
            upgradeUntilCutoff: true,
            upgradeUnknownItems: true);

        Assert.Equal("waiting", held.WantedStatus);
        Assert.Equal("upgrade", upgraded.WantedStatus);
    }

    [Theory]
    [InlineData("Movie.Name.2024.2160p.WEB-DL.DDP5.1", "WEB 2160p")]
    [InlineData("Show.S01E01.1080p.BluRay.x265", "Bluray 1080p")]
    [InlineData("Film.2011.720p.HDTV", "HDTV 720p")]
    [InlineData("Classic.DVDRip", "DVD")]
    public void DetectQuality_normalizes_common_release_names(string releaseName, string expectedQuality)
    {
        Assert.Equal(expectedQuality, LibraryQualityDecider.DetectQuality(releaseName));
    }
}
