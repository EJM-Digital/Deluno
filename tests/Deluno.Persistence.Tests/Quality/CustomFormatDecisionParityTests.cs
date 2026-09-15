using Deluno.Integrations.Search;
using Deluno.Quality;
using Deluno.Quality.Contracts;
using Deluno.Quality.ReleasePreferences;

namespace Deluno.Persistence.Tests.Quality;

public sealed class CustomFormatDecisionParityTests
{
    [Fact]
    public void Dry_run_and_typed_decision_share_one_matcher_for_every_supported_condition_shape()
    {
        var cases = new[]
        {
            new MatchCase("source-web", "[{\"type\":\"source\",\"value\":\"WEB\"}]", null, "Movie.2026.1080p.WEBRip.x264-GRP", false),
            new MatchCase("resolution-4k", "[{\"type\":\"resolution\",\"value\":\"2160p\"}]", null, "Movie.2026.4K.BluRay.x265-GRP", true),
            new MatchCase("codec-av1", "[{\"type\":\"codec\",\"value\":\"AV1\"}]", null, "Movie.2026.1080p.WEB-DL.AV1-GRP", true),
            new MatchCase("language", "[{\"type\":\"language\",\"value\":\"multi\"}]", null, "Movie.2026.MULTI.1080p.WEB-DL-GRP", true),
            new MatchCase("required-and-optional", "[{\"type\":\"source\",\"value\":\"WEB\",\"required\":true},{\"type\":\"hdr\",\"value\":\"DV\",\"required\":false}]", null, "Movie.2026.1080p.WEB-DL-GRP", true),
            new MatchCase("negated", "[{\"type\":\"codec\",\"value\":\"x265\",\"negate\":true,\"required\":true}]", null, "Movie.2026.1080p.WEB-DL.x264-GRP", true),
            new MatchCase("guide-any", "\\bNetflix\\b\n\\bNF\\b", "guide-rule", "Movie.2026.1080p.NF.WEB-DL-GRP", true),
            new MatchCase("radarr-source", "[{\"name\":\"source\",\"implementation\":\"SourceSpecification\",\"fields\":[{\"name\":\"value\",\"value\":\"web-dl\"}],\"negate\":false,\"required\":true}]", null, "Movie.2026.1080p.WEB-DL-GRP", true),
            new MatchCase("radarr-group-regex", "[{\"name\":\"group\",\"implementation\":\"ReleaseGroupSpecification\",\"fields\":[{\"name\":\"value\",\"value\":\"^(DELUNO|TRUSTED)$\"}],\"negate\":false,\"required\":true}]", null, "Movie.2026.1080p.WEB-DL-DELUNO", true)
        };

        foreach (var item in cases)
        {
            var now = DateTimeOffset.Parse("2026-09-15T00:00:00Z");
            var format = new CustomFormatItem(
                item.Id,
                item.Id,
                "movies",
                999999,
                item.TrashId,
                item.Conditions,
                true,
                now,
                now);
            var plan = ReleasePreferencePlanFactory.CreateQualityPlan(
                "movies",
                "WEB 1080p",
                ["WEB 1080p"],
                customFormats: [format],
                formatIntents: new Dictionary<string, string>
                {
                    [format.Id] = ProfileFormatIntents.Prefer
                });

            var dryRun = Assert.Single(CustomFormatMatcher.DryRun(item.ReleaseName, [format]));
            var typed = Assert.Single(ReleasePreferenceFormatEvidenceFactory.Evaluate(plan, item.ReleaseName));

            Assert.Equal(item.Expected, dryRun.IsMatch);
            Assert.Equal(dryRun.IsMatch, typed.IsMatch);
        }
    }

    [Fact]
    public void A_partially_supported_imported_matcher_is_review_only()
    {
        const string definition =
            "[{\"implementation\":\"SourceSpecification\",\"fields\":[{\"name\":\"value\",\"value\":\"web-dl\"}]},"
            + "{\"implementation\":\"IndexerFlagSpecification\",\"fields\":[{\"name\":\"value\",\"value\":\"freeleech\"}]}]";

        Assert.False(CustomFormatDefinitionMatcher.CanEvaluate(definition));
        Assert.False(CustomFormatDefinitionMatcher.Evaluate(
            "Movie.2026.1080p.WEB-DL-GRP",
            definition,
            anyCondition: false).IsMatch);
    }

    [Theory]
    [InlineData("Movie.2026.AMZN.1080p.WEB-DL-GRP", true)]
    [InlineData("Movie.2026.AMZN.1080p.WEBRip-GRP", true)]
    [InlineData("Movie.2026.NF.1080p.WEB-DL-GRP", false)]
    [InlineData("Movie.2026.AMZN.1080p.BluRay-GRP", false)]
    public void Real_trash_radarr_groups_require_every_condition_type_and_accept_enum_sources(
        string releaseName,
        bool expected)
    {
        const string definition =
            "[{\"implementation\":\"ReleaseTitleSpecification\",\"required\":true,\"fields\":{\"value\":\"\\\\b(amzn|amazon(hd)?)\\\\b\"}},"
            + "{\"implementation\":\"SourceSpecification\",\"required\":false,\"fields\":{\"value\":7}},"
            + "{\"implementation\":\"SourceSpecification\",\"required\":false,\"fields\":{\"value\":8}}]";

        Assert.True(CustomFormatDefinitionMatcher.CanEvaluate(definition));
        Assert.Equal(expected, CustomFormatDefinitionMatcher.Evaluate(
            releaseName,
            definition,
            anyCondition: true).IsMatch);
    }

    [Fact]
    public void Contextual_radarr_language_condition_is_not_claimed_as_release_name_evaluable()
    {
        const string definition =
            "[{\"implementation\":\"LanguageSpecification\",\"required\":true,\"fields\":{\"value\":-2}}]";

        Assert.False(CustomFormatDefinitionMatcher.CanEvaluate(definition));
    }

    private sealed record MatchCase(
        string Id,
        string Conditions,
        string? TrashId,
        string ReleaseName,
        bool Expected);
}
