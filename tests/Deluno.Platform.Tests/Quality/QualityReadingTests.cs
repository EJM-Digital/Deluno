using Deluno.Quality;
using Deluno.Quality.ReleasePreferences;

namespace Deluno.Platform.Tests.Quality;

/// <summary>
/// How a release name becomes a quality tier: the low-grade and disc sources first, whole-token matching for the short
/// tags, a tier from source and resolution, nothing guessed when the name says nothing, and the allowed-tier gate that
/// lets an unreadable quality through to the question that answers it. Real classes, no stand-ins. Refs #1296.
/// </summary>
public sealed class QualityReadingTests
{
    [Theory]
    [InlineData("Some.Film.2020.WORKPRINT.x264-GRP", "WORKPRINT")]
    [InlineData("Some.Film.2020.TELESYNC.x264-GRP", "TELESYNC")]
    [InlineData("Some.Film.2020.TS.x264-GRP", "TELESYNC")]
    [InlineData("Some.Film.2020.TELECINE.x264-GRP", "TELECINE")]
    [InlineData("Some.Film.2020.TC.x264-GRP", "TELECINE")]
    [InlineData("Some.Film.2020.DVDSCR.x264-GRP", "DVDSCR")]
    [InlineData("Some.Film.2020.SCREENER.x264-GRP", "DVDSCR")]
    [InlineData("Some.Film.2020.REGIONAL.DVD.x264-GRP", "REGIONAL")]
    [InlineData("Some.Film.2020.CAMRip.x264-GRP", "CAM")]
    [InlineData("Some.Film.2020.CAM.x264-GRP", "CAM")]
    [InlineData("Some.Film.2020.BR-DISK-GRP", "BR-DISK")]
    [InlineData("Some.Film.2020.BDMV-GRP", "BR-DISK")]
    [InlineData("Some.Film.2020.BDISO-GRP", "BR-DISK")]
    [InlineData("Some.Film.2020.RawHD-GRP", "Raw-HD")]
    [InlineData("Some.Film.2020.Raw-HD-GRP", "Raw-HD")]
    [InlineData("Some.Film.2020.DVD-R-GRP", "DVD-R")]
    [InlineData("Some.Film.2020.DVDR-GRP", "DVD-R")]
    public void The_low_grade_and_disc_sources_are_read_from_the_name(string name, string quality)
        => Assert.Equal(quality, LibraryQualityDecider.DetectQuality(name));

    [Fact]
    public void A_low_grade_source_wins_over_the_resolution_it_also_names()
    {
        Assert.Equal("CAM", LibraryQualityDecider.DetectQuality("Some.Film.2020.1080p.CAM.x264-GRP"));
        Assert.Equal("TELESYNC", LibraryQualityDecider.DetectQuality("Some.Film.2020.2160p.WEB-DL.TS.x265-GRP"));
        Assert.Equal("DVDSCR", LibraryQualityDecider.DetectQuality("Some.Film.2020.720p.BluRay.SCREENER-GRP"));
    }

    [Theory]
    [InlineData("Camera.Obscura.2015.1080p.BluRay.x264-GRP", "Bluray 1080p")]
    [InlineData("Ghosts.of.Mars.2001.720p.BluRay.x264-GRP", "Bluray 720p")]
    [InlineData("Tcheky.Karyo.1998.1080p.WEB-DL.x264-GRP", "WEB 1080p")]
    [InlineData("Cameron.Crowe.Doc.2005.720p.HDTV.x264-GRP", "HDTV 720p")]
    public void A_short_tag_inside_a_longer_word_is_not_a_low_grade_source(string name, string quality)
        => Assert.Equal(quality, LibraryQualityDecider.DetectQuality(name));

    [Theory]
    [InlineData("Some.Film.2020.TS.x264-GRP", "TELESYNC")]
    [InlineData("Some Film 2020 TC x264", "TELECINE")]
    [InlineData("Some-Film-2020-CAM-x264", "CAM")]
    [InlineData("Some_Film_2020_(CAM)_x264", "CAM")]
    [InlineData("Some.Film.2020.[TS].x264", "TELESYNC")]
    public void The_short_tags_count_as_whole_words_between_any_of_the_usual_separators(string name, string quality)
        => Assert.Equal(quality, LibraryQualityDecider.DetectQuality(name));

    [Theory]
    [InlineData("Some.Film.2020.2160p.REMUX.x265-GRP", "Remux 2160p")]
    [InlineData("Some.Film.2020.2160p.UHD.BluRay.x265-GRP", "Bluray 2160p")]
    [InlineData("Some.Film.2020.2160p.WEB-DL.x265-GRP", "WEB 2160p")]
    [InlineData("Some.Film.2020.2160p.HDTV.x265-GRP", "HDTV 2160p")]
    [InlineData("Some.Film.2020.1080p.REMUX.x264-GRP", "Remux 1080p")]
    [InlineData("Some.Film.2020.1080p.BluRay.x264-GRP", "Bluray 1080p")]
    [InlineData("Some.Film.2020.1080p.WEBRip.x264-GRP", "WEB 1080p")]
    [InlineData("Some.Film.2020.1080p.HDTV.x264-GRP", "HDTV 1080p")]
    [InlineData("Some.Film.2020.720p.BluRay.x264-GRP", "Bluray 720p")]
    [InlineData("Some.Film.2020.720p.WEB-DL.x264-GRP", "WEB 720p")]
    [InlineData("Some.Film.2020.720p.HDTV.x264-GRP", "HDTV 720p")]
    [InlineData("Some.Film.2020.576p.BluRay.x264-GRP", "Bluray 576p")]
    [InlineData("Some.Film.2020.480p.BluRay.x264-GRP", "Bluray 480p")]
    [InlineData("Some.Film.2020.480p.WEB-DL.x264-GRP", "WEB 480p")]
    [InlineData("Some.Film.2020.DVDRip.x264-GRP", "DVD")]
    [InlineData("Some.Show.S01E01.SDTV.x264-GRP", "SDTV")]
    public void An_ordinary_name_reads_as_its_source_and_resolution_tier(string name, string quality)
        => Assert.Equal(quality, LibraryQualityDecider.DetectQuality(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Some.Film.2020.x264-GRP")]
    public void A_name_that_says_nothing_has_no_quality_and_nothing_is_guessed(string? name)
        => Assert.Null(LibraryQualityDecider.DetectQuality(name));

    [Theory]
    [InlineData("Some.Film.2020.HDCAM.x264-GRP", "CAM")]
    [InlineData("Some.Film.2020.CAMRip.x264-GRP", "CAM")]
    [InlineData("Some.Film.2020.TELESYNC.x264-GRP", "TELESYNC")]
    [InlineData("Some.Film.2020.HDTS.x264-GRP", "TELESYNC")]
    [InlineData("Some.Film.2020.TS.x264-GRP", "TELESYNC")]
    [InlineData("Some.Film.2020.1080p.BluRay.x264-GRP", "Bluray 1080p")]
    [InlineData("Some.Film.2020.1080p.BRRip.x264-GRP", "Bluray 1080p")]
    [InlineData("Some.Film.2020.1080p.WEB-DL.x264-GRP", "WEB 1080p")]
    [InlineData("Some.Film.2020.1080p.x264-GRP", "HDTV 1080p")]
    [InlineData("Some.Film.2020.720p.x264-GRP", "HDTV 720p")]
    [InlineData("Some.Film.2020.480p.x264-GRP", "SDTV")]
    [InlineData("Some.Film.2020.DVDRip.x264-GRP", "DVD")]
    [InlineData("Some.Film.2020.REMUX.2160p.x265-GRP", "Remux 2160p")]
    [InlineData("Some.Film.2020.REMUX.x265-GRP", "Remux 1080p")]
    [InlineData("Some.Film.2020 - Featurettes", "Unknown")]
    [InlineData("", "Unknown")]
    [InlineData(null, "Unknown")]
    public void The_release_quality_search_judges_by_is_read_from_the_name_and_unknown_when_it_says_nothing(string? name, string quality)
        => Assert.Equal(quality, ReleaseNameQuality.Read(name));

    [Fact]
    public void A_candidate_whose_quality_cannot_be_read_is_not_refused_by_the_allowed_gate_but_a_known_one_outside_the_list_is()
    {
        string[] allowed = ["WEB 1080p", "Bluray 1080p"];

        Assert.True(AllowedQualityGate.Accepts(allowed, null));
        Assert.True(AllowedQualityGate.Accepts(allowed, ""));
        Assert.True(AllowedQualityGate.Accepts(allowed, "   "));
        Assert.True(AllowedQualityGate.Accepts(allowed, "web 1080p"));
        Assert.False(AllowedQualityGate.Accepts(allowed, "WEB 2160p"));
        Assert.True(AllowedQualityGate.Accepts([], "WEB 2160p"));
        Assert.True(AllowedQualityGate.Accepts(null, "WEB 2160p"));
        Assert.Equal(
            "WEB 2160p is not one of the qualities this profile allows (WEB 1080p, Bluray 1080p).",
            AllowedQualityGate.Refusal(allowed, "WEB 2160p"));
    }

    [Theory]
    [InlineData("Show.S01E02.1080p.WEB-DL.x264-GRP", false, true, 1, 2, null)]
    [InlineData("Show.S01E02E03.1080p.WEB-DL.x264-GRP", false, true, 1, 2, 3)]
    [InlineData("Show.S01E02-E04.1080p.WEB-DL.x264-GRP", false, true, 1, 2, 4)]
    [InlineData("Show.S02.1080p.WEB-DL.x264-GRP", true, false, 2, null, null)]
    [InlineData("Show.Season.S03.1080p.WEB-DL.x264-GRP", true, false, 3, null, null)]
    public void A_show_release_is_a_single_episode_a_run_of_episodes_or_a_whole_season(
        string name, bool isSeason, bool isEpisode, int season, int? episode, int? end)
    {
        var classification = ReleaseEpisodeClassifier.Classify(name);

        Assert.Equal(isSeason, classification.IsSeason);
        Assert.Equal(isEpisode, classification.IsEpisode);
        Assert.Equal(season, classification.Season);
        Assert.Equal(episode, classification.Episode);
        Assert.Equal(end, classification.EpisodeEnd);
    }

    [Theory]
    [InlineData("Show.S01E02.1080p.WEB-DL.x264-GRP", "single episode")]
    [InlineData("Show.S01E02E03.1080p.WEB-DL.x264-GRP", "multi-episode")]
    [InlineData("Show.S02.1080p.WEB-DL.x264-GRP", "season pack")]
    [InlineData("Some.Film.2020.1080p.WEB-DL.x264-GRP", null)]
    public void The_release_type_names_the_kind_of_release_or_nothing(string name, string? type)
        => Assert.Equal(type, ReleaseEpisodeClassifier.ReleaseType(name));

    [Fact]
    public void A_release_covers_an_episode_when_it_is_that_episode_a_run_that_spans_it_or_its_whole_season()
    {
        Assert.True(ReleaseEpisodeClassifier.CoversEpisode("Show.S01E02.1080p.WEB-DL-GRP", 1, 2));
        Assert.False(ReleaseEpisodeClassifier.CoversEpisode("Show.S01E02.1080p.WEB-DL-GRP", 1, 3));
        Assert.False(ReleaseEpisodeClassifier.CoversEpisode("Show.S01E02.1080p.WEB-DL-GRP", 2, 2));
        Assert.True(ReleaseEpisodeClassifier.CoversEpisode("Show.S01E02E04.1080p.WEB-DL-GRP", 1, 3));
        Assert.False(ReleaseEpisodeClassifier.CoversEpisode("Show.S01E02E04.1080p.WEB-DL-GRP", 1, 5));
        Assert.True(ReleaseEpisodeClassifier.CoversEpisode("Show.S02.1080p.WEB-DL-GRP", 2, 7));
        Assert.False(ReleaseEpisodeClassifier.CoversEpisode("Show.S02.1080p.WEB-DL-GRP", 3, 7));
        Assert.False(ReleaseEpisodeClassifier.CoversEpisode("Some.Film.2020.1080p.WEB-DL-GRP", 1, 1));
    }

    [Fact]
    public void A_source_word_inside_a_longer_word_of_the_title_is_not_a_source()
    {
        Assert.Equal("HDTV 720p", LibraryQualityDecider.DetectQuality("Webster.S01E01.720p.HDTV.x264-GRP"));
        Assert.Equal("WEB 1080p", LibraryQualityDecider.DetectQuality("Regional.Manager.S01E01.1080p.WEB-DL.x264-GRP"));
        Assert.Equal("Bluray 1080p", LibraryQualityDecider.DetectQuality("The.Screener.2019.1080p.BluRay.x264-GRP"));
    }

    [Theory]
    [InlineData("Movie.2020.1080p.WEB-DL.x264-GRP", "WEB 1080p")]
    [InlineData("Movie.2020.1080p.WEBRip.x264-GRP", "WEB 1080p")]
    [InlineData("Movie.2020.720p.WEBDL.x264-GRP", "WEB 720p")]
    [InlineData("Movie.2020.DVDRip.x264-GRP", "DVD")]
    [InlineData("Movie.2020.NTSC.DVD9-GRP", "DVD")]
    [InlineData("Movie.2020.DVDSCR.x264-GRP", "DVDSCR")]
    [InlineData("Movie.2020.SCREENER.x264-GRP", "DVDSCR")]
    [InlineData("Movie.2020.REGIONAL.DVDRip-GRP", "REGIONAL")]
    [InlineData("Movie.2020.1080p.Blu-Ray.x264-GRP", "Bluray 1080p")]
    [InlineData("Movie.2020.2160p.REMUX.x265-GRP", "Remux 2160p")]
    [InlineData("Show.S01E01.720p.HDTV.x264-GRP", "HDTV 720p")]
    [InlineData("WEB 1080p", "WEB 1080p")]
    [InlineData("Movie.2020.CAM.x264-GRP", "CAM")]
    public void Real_scene_source_words_are_still_read(string name, string expected)
        => Assert.Equal(expected, LibraryQualityDecider.DetectQuality(name));
}
