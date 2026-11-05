using Deluno.Quality.ReleasePreferences;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>
/// #1505: the preference facts and the custom-format "resolution" condition read a release's resolution through the one shared
/// reader, so an explicit token beats a UHD or 4K source tag in both, and every resolution a condition can name is answered.
/// </summary>
public sealed class AResolutionTokenBeatsAUhdSourceTagInEveryReaderTests
{
    private static string Conditions(string type, string value)
        => $"[{{\"type\":\"{type}\",\"value\":\"{value}\",\"negate\":false,\"required\":true}}]";

    private static bool Matches(string releaseName, string type, string value)
        => CustomFormatDefinitionMatcher.Evaluate(releaseName, Conditions(type, value), anyCondition: false, "movies").IsMatch;

    private static string? ResolutionFact(string releaseName)
    {
        var plan = ReleasePreferencePlanFactory.CreateQualityPlan("movies", "WEB 1080p", ["WEB 1080p"]);
        var facts = ReleasePreferenceFactFactory.FromReleaseName(plan, releaseName, quality: "WEB 1080p");
        return facts.SingleOrDefault(fact => fact.NormalizedTraitId.StartsWith("video.resolution", StringComparison.Ordinal))?.NormalizedTraitId;
    }

    [Theory]
    [InlineData("Film.2024.UHD.BluRay.1080p.x265-GRP", "2160p", false)]
    [InlineData("Film.2024.UHD.BluRay.1080p.x265-GRP", "1080p", true)]
    [InlineData("Film.2024.UHD.BluRay.x265-GRP", "2160p", true)]
    [InlineData("Film.2024.4K.WEB-DL.x265-GRP", "2160p", true)]
    [InlineData("Film.2024.2160p.WEB-DL.x265-GRP", "2160p", true)]
    [InlineData("Film.2024.720p.WEB-DL.x264-GRP", "720p", true)]
    [InlineData("Film.2024.DVDRip.480p.x264-GRP", "480p", true)]
    [InlineData("Film.2024.576p.PAL.x264-GRP", "576p", true)]
    [InlineData("Film.2024.720p.WEB-DL.x264-GRP", "2160p", false)]
    public void A_resolution_condition_reads_the_resolution_the_shared_reader_gives(string name, string value, bool expected)
        => Assert.Equal(expected, Matches(name, "resolution", value));

    [Theory]
    [InlineData("Film.2024.HDR10+.2160p-GRP", "hdr10+", true)]
    [InlineData("Film.2024.HDR10.2160p-GRP", "hdr10", true)]
    [InlineData("Film.2024.HLG.2160p-GRP", "hlg", true)]
    [InlineData("Film.2024.DV.2160p-GRP", "dv", true)]
    [InlineData("Film.2024.2160p-GRP", "hdr10", false)]
    [InlineData("Film.2024.PQ10.2160p-GRP", "pq10", true)]
    [InlineData("Film.2024.x265-GRP", "hevc", true)]
    [InlineData("Film.2024.x264-GRP", "avc", true)]
    [InlineData("Film.2024.AV1-GRP", "av1", true)]
    [InlineData("Film.2024.XviD-GRP", "divx", true)]
    [InlineData("Film.2024.x264-GRP", "vp9", false)]
    public void Hdr_and_codec_conditions_answer_each_value_they_name(string name, string value, bool expected)
        => Assert.Equal(expected, Matches(name, name.Contains("x26", StringComparison.Ordinal) || name.Contains("AV1", StringComparison.Ordinal) || name.Contains("XviD", StringComparison.Ordinal) || value == "vp9" ? "codec" : "hdr", value));

    [Theory]
    [InlineData("Film.2024.UHD.BluRay.1080p.x265-GRP", "1080p")]
    [InlineData("Film.2024.UHD.BluRay.x265-GRP", "2160p")]
    [InlineData("Film.2024.4K.WEB-DL.x265-GRP", "2160p")]
    [InlineData("Film.2024.720p.WEB-DL.x264-GRP", "720p")]
    [InlineData("Film.2024.576p.PAL.x264-GRP", "576p")]
    [InlineData("Film.2024.480p.DVDRip.x264-GRP", "480p")]
    [InlineData("Film.2024.WEB-DL.x264-GRP", null)]
    public void The_resolution_fact_of_a_release_comes_from_the_shared_reader(string name, string? resolution)
    {
        var trait = ResolutionFact(name);

        if (resolution is null)
        {
            Assert.Null(trait);
        }
        else
        {
            Assert.NotNull(trait);
            Assert.Contains(resolution.Replace("p", string.Empty), trait, StringComparison.Ordinal);
        }
    }
}
