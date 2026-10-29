using Deluno.Quality;
using Deluno.Quality.ReleasePreferences;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>
/// The low-severity matcher and name-reading rows of #1329: what a custom format's conditions mean for a release name,
/// the plain words a rule that cannot match gives, and what a release name says about source, edition, revision and language.
/// </summary>
public sealed class QualityMatcherLowGapTests
{
    private static string Conditions(string type, string value, bool required = true)
        => $"[{{\"type\":\"{type}\",\"value\":\"{value}\",\"negate\":false,\"required\":{(required ? "true" : "false")}}}]";

    private static bool Matches(string releaseName, string type, string value, string mediaType = "movies")
        => CustomFormatDefinitionMatcher.Evaluate(releaseName, Conditions(type, value), anyCondition: false, mediaType).IsMatch;

    [Fact]
    public void A_rule_that_cannot_be_read_never_matches_and_says_why_in_plain_words()
    {
        var empty = CustomFormatDefinitionMatcher.Evaluate("Film.2020.1080p.WEB-DL-GRP", "", false);
        var blank = CustomFormatDefinitionMatcher.Evaluate("Film.2020.1080p.WEB-DL-GRP", "[]", false);
        var blankValue = CustomFormatDefinitionMatcher.Evaluate("Film.2020.1080p.WEB-DL-GRP", Conditions("source", ""), false);
        var unsupported = CustomFormatDefinitionMatcher.Evaluate("Film.2020.1080p.WEB-DL-GRP", Conditions("telepathy", "web"), false);
        var noName = CustomFormatDefinitionMatcher.Evaluate("  ", Conditions("source", "web"), false);

        Assert.All(new[] { empty, blank, blankValue, unsupported, noName }, match => Assert.False(match.IsMatch));
        Assert.Equal(["No conditions defined"], empty.MissedConditions);
        Assert.Equal(["No complete supported condition definition"], blank.MissedConditions);
        Assert.Equal(["No complete supported condition definition"], blankValue.MissedConditions);
        Assert.Equal(["No complete supported condition definition"], unsupported.MissedConditions);
        Assert.Equal(["Release name is empty"], noName.MissedConditions);
    }

    [Theory]
    [InlineData("remux", "Film.2020.1080p.BluRay.REMUX.AVC-GRP", true)]
    [InlineData("remux", "Film.2020.1080p.BluRay.x264-GRP", false)]
    [InlineData("hdtv", "Show.S01E01.720p.PDTV.x264-GRP", true)]
    [InlineData("hdtv", "Show.S01E01.SDTV.XviD-GRP", true)]
    [InlineData("hdtv", "Show.S01E01.TVRip.XviD-GRP", true)]
    [InlineData("hdtv", "Show.S01E01.720p.WEB.x264-GRP", false)]
    [InlineData("rawhd", "Show.S01E01.1080i.RAWHD.MPEG2-GRP", true)]
    [InlineData("rawhd", "Show.S01E01.1080i.Raw.HD.MPEG2-GRP", true)]
    [InlineData("regional", "Film.2020.R5.Regional.DVDRip-GRP", true)]
    [InlineData("regional", "Regional.Manager.2020.1080p.WEB-DL-GRP", false)]
    [InlineData("screener", "Film.2020.DVD.Screener.x264-GRP", true)]
    [InlineData("screener", "Film.2020.SCR.x264-GRP", true)]
    [InlineData("brdisk", "Film.2020.BRDISK-GRP", true)]
    [InlineData("brdisk", "Film.2020.BR.DISK-GRP", true)]
    [InlineData("bluray-encode", "Film.2020.1080p.BluRay.x264-GRP", true)]
    [InlineData("bluray-encode", "Film.2020.1080p.BluRay.REMUX.AVC-GRP", false)]
    [InlineData("dvd", "Film.2020.DVDRip.x264-GRP", true)]
    [InlineData("dvd", "Film.2020.1080p.WEB-DL-GRP", false)]
    public void Source_condition_values_read_the_release_names_tags(string value, string releaseName, bool expected)
        => Assert.Equal(expected, Matches(releaseName, "source", value));

    [Theory]
    [InlineData("1080p", "Film.2020.1080p.WEB-DL-GRP", true)]
    [InlineData("2160p", "Film.2020.2160p.WEB-DL-GRP", true)]
    [InlineData("2160p", "Film.2020.4K.WEB-DL-GRP", true)]
    [InlineData("2160p", "Film.2020.1080p.WEB-DL-GRP", false)]
    [InlineData("720p", "Film.2020.720p.WEB-DL-GRP", true)]
    [InlineData("480p", "Film.2020.480p.WEB-DL-GRP", true)]
    [InlineData("480p", "Film.2020.720p.WEB-DL-GRP", false)]
    [InlineData("576p", "Film.2020.576p.WEB-DL-GRP", true)]
    public void Resolution_condition_matches_the_stated_resolution_and_4k_counts_as_2160p(string value, string releaseName, bool expected)
        => Assert.Equal(expected, Matches(releaseName, "resolution", value));

    [Theory]
    [InlineData("dv", "Film.2020.2160p.WEB-DL.DV.HEVC-GRP", true)]
    [InlineData("dolbyvision", "Film.2020.2160p.WEB-DL.Dolby.Vision.HEVC-GRP", true)]
    [InlineData("dv", "Film.2020.2160p.WEB-DL.HDR10.HEVC-GRP", false)]
    [InlineData("hdr10", "Film.2020.2160p.WEB-DL.HDR10.HEVC-GRP", true)]
    [InlineData("hdr10plus", "Film.2020.2160p.WEB-DL.HDR10+.HEVC-GRP", true)]
    [InlineData("hdr10plus", "Film.2020.2160p.WEB-DL.HDR10Plus.HEVC-GRP", true)]
    [InlineData("hlg", "Film.2020.2160p.HDTV.HLG.HEVC-GRP", true)]
    [InlineData("hlg", "Film.2020.2160p.WEB-DL.HEVC-GRP", false)]
    public void Hdr_condition_values_read_dolby_vision_hdr10_hdr10plus_and_hlg(string value, string releaseName, bool expected)
        => Assert.Equal(expected, Matches(releaseName, "hdr", value));

    [Theory]
    [InlineData("x265", "Film.2020.1080p.WEB-DL.x265-GRP", true)]
    [InlineData("hevc", "Film.2020.1080p.WEB-DL.x265-GRP", true)]
    [InlineData("x265", "Film.2020.1080p.WEB-DL.HEVC-GRP", true)]
    [InlineData("x265", "Film.2020.1080p.WEB-DL.H265-GRP", true)]
    [InlineData("av1", "Film.2020.1080p.WEB-DL.AV1-GRP", true)]
    [InlineData("x264", "Film.2020.1080p.WEB-DL.H264-GRP", true)]
    [InlineData("avc", "Film.2020.1080p.WEB-DL.x264-GRP", true)]
    [InlineData("xvid", "Film.2020.DVDRip.DivX-GRP", true)]
    [InlineData("divx", "Film.2020.DVDRip.XviD-GRP", true)]
    [InlineData("x265", "Film.2020.1080p.WEB-DL.x264-GRP", false)]
    public void Codec_condition_reads_the_aliases_of_each_codec(string value, string releaseName, bool expected)
        => Assert.Equal(expected, Matches(releaseName, "codec", value));

    [Fact]
    public void An_unreadable_pattern_or_a_bad_modifier_is_described_in_plain_words_and_matches_as_its_own_literal_text()
    {
        Assert.Null(ReleaseTermPattern.Validate("plain words"));
        Assert.Null(ReleaseTermPattern.Validate("/web[- ]?dl/i"));
        Assert.Equal(
            "'/(unclosed/' is not a valid pattern. Check the brackets and slashes, or write it as plain words.",
            ReleaseTermPattern.Validate("/(unclosed/"));
        Assert.Equal(
            "'/web/z' ends with the letter 'z', which a pattern cannot use. Patterns can end in i, m, s, x or n.",
            ReleaseTermPattern.Validate("/web/z"));

        Assert.True(ReleaseTermPattern.IsMatch("Film.2020./(unclosed/.x264", "/(unclosed/"));
        Assert.False(ReleaseTermPattern.IsMatch("Film.2020.1080p.WEB-DL", "/(unclosed/"));
        Assert.True(ReleaseTermPattern.IsMatch("Film.2020./web/z.x264", "/web/z"));
        Assert.False(ReleaseTermPattern.IsMatch("Film.2020.1080p.WEB-DL", "/web/z"));
    }

    [Theory]
    [InlineData("Film.2020.1080p.BluRay.REMUX.AVC-GRP", "Remux")]
    [InlineData("Film.2020.1080p.BluRay.x264-GRP", "Bluray")]
    [InlineData("Film.2020.1080p.WEB-DL.x264-GRP", "WEB")]
    [InlineData("Show.S01E01.720p.HDTV.x264-GRP", "HDTV")]
    [InlineData("Film.2020.DVDRip.XviD-GRP", "DVD")]
    [InlineData("Film.2020.HDRip.XviD-GRP", "HDRip")]
    [InlineData("Film.2020.CAM.XviD-GRP", "CAM")]
    public void A_source_is_read_from_a_name_with_the_most_specific_token_first(string releaseName, string expected)
        => Assert.Equal(expected, MediaFileNameFacts.Parse(releaseName).Source);

    [Theory]
    [InlineData("Film.2020.Directors.Cut.1080p.BluRay-GRP", "Director's Cut")]
    [InlineData("Film.2020.EXTENDED.1080p.BluRay-GRP", "Extended")]
    [InlineData("Film.2020.Theatrical.Cut.1080p.BluRay-GRP", "Theatrical")]
    [InlineData("Film.2020.Ultimate.Edition.1080p.BluRay-GRP", "Ultimate Edition")]
    [InlineData("Film.2020.Special.Edition.1080p.BluRay-GRP", "Special Edition")]
    [InlineData("Film.2020.Redux.1080p.BluRay-GRP", "Redux")]
    [InlineData("Film.2020.Remastered.1080p.BluRay-GRP", "Remastered")]
    [InlineData("Film.2020.UNRATED.1080p.BluRay-GRP", "Unrated")]
    [InlineData("Film.2020.UNCUT.1080p.BluRay-GRP", "Uncut")]
    [InlineData("Film.2020.IMAX.1080p.BluRay-GRP", "IMAX")]
    public void Edition_tags_are_read_as_their_own_token(string releaseName, string expected)
        => Assert.Contains(expected, MediaFileNameFacts.Parse(releaseName).Editions);

    [Theory]
    [InlineData("Film.2020.1080p.WEB-DL.x264-GRP")]
    [InlineData("Film.2020.PROPER.1080p.WEB-DL.x264-GRP")]
    public void A_name_with_no_edition_tag_reads_no_edition(string releaseName)
        => Assert.Empty(MediaFileNameFacts.Parse(releaseName).Editions);

    [Theory]
    [InlineData("Film.2020.1080p.WEB-DL.x264-GRP", "none")]
    [InlineData("Film.2020.PROPER.1080p.WEB-DL.x264-GRP", "Proper")]
    [InlineData("Film.2020.REPACK.1080p.WEB-DL.x264-GRP", "Proper")]
    [InlineData("Film.2020.REPACK2.1080p.WEB-DL.x264-GRP", "Repack 2")]
    [InlineData("Film.2020.REPACK.3.1080p.WEB-DL.x264-GRP", "Repack 3")]
    public void Revision_detection_gives_proper_for_proper_or_repack_and_the_higher_levels_for_repack_2_and_3(string releaseName, string expected)
    {
        var plan = ReleasePreferencePlanFactory.CreateQualityPlan("movies", "WEB 1080p", ["WEB 1080p"]);

        var facts = ReleasePreferenceFactFactory.FromReleaseName(plan, releaseName);

        var revisions = new[] { "Proper", "Repack 2", "Repack 3" }
            .Where(level => PreferenceTraitRegistry.Current.TryResolveObserved("release.revision", level, out var definition)
                && facts.Any(fact => fact.NormalizedTraitId == definition.Id.ToLowerInvariant() && fact.State == PreferenceFactState.Present))
            .ToArray();
        Assert.Equal(expected == "none" ? [] : [expected], revisions);
    }

    [Theory]
    [InlineData("Film.2020.MULTi.1080p.WEB-DL-GRP", "Multilingual")]
    [InlineData("Film.2020.DUAL.1080p.WEB-DL-GRP", "Dubbed")]
    [InlineData("Film.2020.DUBBED.1080p.WEB-DL-GRP", "Dubbed")]
    [InlineData("Film.2020.ORIGINAL.1080p.WEB-DL-GRP", "Original language")]
    [InlineData("Film.2020.FRENCH.1080p.WEB-DL-GRP", "French")]
    [InlineData("Film.2020.VOSTFR.1080p.WEB-DL-GRP", "French")]
    [InlineData("Film.2020.VFF.1080p.WEB-DL-GRP", "French")]
    [InlineData("Film.2020.GERMAN.1080p.WEB-DL-GRP", "German")]
    [InlineData("Film.2020.JAPANESE.1080p.WEB-DL-GRP", "Japanese")]
    [InlineData("Film.2020.SPANISH.1080p.WEB-DL-GRP", "Spanish")]
    [InlineData("Film.2020.LATINO.1080p.WEB-DL-GRP", "Spanish")]
    [InlineData("Film.2020.ENGLISH.1080p.WEB-DL-GRP", "English")]
    [InlineData("Film.2020.1080p.WEB-DL-GRP", null)]
    public void Language_detection_reads_multi_dual_dubbed_original_and_the_named_languages(string releaseName, string? expected)
        => Assert.Equal(expected, ReleasePreferenceFactFactory.DetectLanguageFromReleaseName(releaseName));
}
