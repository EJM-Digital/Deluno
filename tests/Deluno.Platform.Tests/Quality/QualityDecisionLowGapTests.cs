using Deluno.Quality;

namespace Deluno.Platform.Tests.Quality;

/// <summary>
/// The low-severity quality-decision rows of #1329: how a file is decided when the cutoff or the quality is missing,
/// how the media label picks the engine, how an old policy snapshot is migrated, and how names and tags are read.
/// </summary>
public sealed class QualityDecisionLowGapTests
{
    [Fact]
    public void A_file_of_unknown_quality_with_upgrade_unknown_on_but_no_cutoff_is_covered()
    {
        var decision = LibraryQualityDecider.Decide("movie", hasFile: true, currentQuality: null, cutoffQuality: null,
            upgradeUntilCutoff: true, upgradeUnknownItems: true);

        Assert.Equal("covered", decision.WantedStatus);
        Assert.False(decision.QualityCutoffMet);
        Assert.Null(decision.CurrentQuality);
        Assert.Null(decision.TargetQuality);
        Assert.Equal("This movie is already in your library.", decision.WantedReason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_file_with_a_known_quality_but_no_cutoff_is_covered_and_names_its_quality(bool upgradeUntilCutoff)
    {
        var decision = LibraryQualityDecider.Decide("movie", hasFile: true, currentQuality: "WEB 720p", cutoffQuality: null,
            upgradeUntilCutoff, upgradeUnknownItems: true);

        Assert.Equal("covered", decision.WantedStatus);
        Assert.False(decision.QualityCutoffMet);
        Assert.Equal("WEB 720p", decision.CurrentQuality);
        Assert.Equal("This movie is currently WEB 720p.", decision.WantedReason);
    }

    [Theory]
    [InlineData("TV", "TV show")]
    [InlineData("tv series", "TV show")]
    [InlineData("Daily TV Shows", "TV show")]
    [InlineData("movie", "movie")]
    [InlineData("episode", "movie")]
    [InlineData("Anime", "movie")]
    public void The_media_label_picks_the_tv_engine_only_when_it_contains_tv(string label, string expectedWord)
    {
        var decision = LibraryQualityDecider.Decide(label, hasFile: false, currentQuality: null, cutoffQuality: "WEB 1080p",
            upgradeUntilCutoff: true, upgradeUnknownItems: true);

        Assert.Equal($"Deluno is still looking for this {expectedWord}.", decision.WantedReason);
    }

    [Fact]
    public void A_policy_snapshot_already_at_the_current_version_is_returned_unchanged()
    {
        var engine = new VersionedMediaPolicyEngine();
        var snapshot = new MediaPolicySnapshot(engine.CurrentVersion, "web 1080p", ["web 720p"], true, false);

        var result = engine.Migrate(snapshot);

        Assert.False(result.Changed);
        Assert.Same(snapshot, result.Snapshot);
        Assert.Equal(["Policy is already current."], result.Notes);
    }

    [Fact]
    public void A_policy_snapshot_migration_falls_back_to_the_default_cutoff_and_allowed_tiers()
    {
        var engine = new VersionedMediaPolicyEngine();
        var snapshot = new MediaPolicySnapshot("media-policy/v0", "Gold plated", [], true, true);

        var result = engine.Migrate(snapshot);

        Assert.True(result.Changed);
        Assert.Equal("WEB 1080p", result.Snapshot.CutoffQuality);
        Assert.Equal(["WEB 720p", "WEB 1080p", "Bluray 1080p", "WEB 2160p", "Bluray 2160p"], result.Snapshot.AllowedQualities);
        Assert.Equal(engine.CurrentVersion, result.Snapshot.Version);
        Assert.Equal("media-policy/v0", result.FromVersion);
    }

    [Theory]
    [InlineData("Film.2020.2160p.BluRay.REMUX.HEVC", "Remux 2160p")]
    [InlineData("Show.S01E01.2160p.HDTV.x265", "HDTV 2160p")]
    [InlineData("Show.S01E01.720p.WEB.h264", "WEB 720p")]
    [InlineData("Film.2020.720p.BluRay.x264", "Bluray 720p")]
    [InlineData("Film.2020.576p.BluRay.x264", "Bluray 576p")]
    [InlineData("Film.2020.480p.BluRay.x264", "Bluray 480p")]
    [InlineData("Show.S01E01.480p.WEB.h264", "WEB 480p")]
    [InlineData("Show.S01E01.1080p.HDTV.x264", "HDTV 1080p")]
    [InlineData("Show.S01E01.SDTV.XviD", "SDTV")]
    [InlineData("Some Plain Title", null)]
    public void DetectQuality_reads_the_remaining_source_and_resolution_pairs(string name, string? expected)
        => Assert.Equal(expected, LibraryQualityDecider.DetectQuality(name));

    [Theory]
    [InlineData("Film.2020.576p.WEB-DL.x264", "WEB 480p")]
    [InlineData("Film.2020.480p.WEB-DL.x264", "WEB 480p")]
    [InlineData("Show.S01E01.HDTV.x264", "SDTV")]
    [InlineData("Film.2020.REMUX.AVC", "Remux 1080p")]
    [InlineData("Film.2020.WEB-DL.x264", "Unknown")]
    public void ReleaseNameQuality_read_maps_the_low_resolution_and_missing_resolution_cases(string name, string expected)
        => Assert.Equal(expected, ReleaseNameQuality.Read(name));

    [Theory]
    [InlineData("de", "deu", true)]
    [InlineData("de", "GER", true)]
    [InlineData("fr", "fra", true)]
    [InlineData("fr", "fre", true)]
    [InlineData("es", "spa", true)]
    [InlineData("es", "eng", false)]
    [InlineData("original", "eng", false)]
    [InlineData("multi", "eng", false)]
    [InlineData("dubbed", "deu", false)]
    public void AudioLanguageMatch_uses_aliases_and_never_matches_assembly_codes(string wanted, string tag, bool expected)
        => Assert.Equal(expected, AudioLanguageMatch.AnyMatches([tag], wanted));

    [Theory]
    [InlineData("de", true)]
    [InlineData("fr", true)]
    [InlineData("es", true)]
    [InlineData("original", false)]
    [InlineData("multi", false)]
    [InlineData("dubbed", false)]
    public void AudioLanguageMatch_recognizes_only_real_languages(string code, bool expected)
        => Assert.Equal(expected, AudioLanguageMatch.Recognizes(code));

    [Fact]
    public void QualityTypicalSizes_gives_the_widest_band_to_an_unknown_tier_and_the_table_band_to_a_known_one()
    {
        Assert.Equal((0.1, 130.0), QualityTypicalSizes.FilmSizeGb("Gold plated"));
        Assert.Equal((50.0, 36000.0), QualityTypicalSizes.EpisodeSizeMb("Gold plated"));
        Assert.Equal((0.1, 130.0), QualityTypicalSizes.FilmSizeGb(null));
        Assert.Equal((1.5, 25.0), QualityTypicalSizes.FilmSizeGb("web 1080p"));
        Assert.Equal((420.0, 3800.0), QualityTypicalSizes.EpisodeSizeMb("WEB 1080p"));
        var tier = QualityTypicalSizes.For("WEB 1080p")!;
        Assert.Equal(1.5 + (25.0 - 1.5) * 0.6, tier.MoviePreferredGb, 3);
    }
}
