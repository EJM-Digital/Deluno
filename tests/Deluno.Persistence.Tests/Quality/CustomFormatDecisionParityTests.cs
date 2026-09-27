using System.Text.Json.Nodes;
using Deluno.Integrations.Search;
using Deluno.Quality;
using Deluno.Quality.Contracts;
using Deluno.Quality.Guides;
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

    /// <summary>
    /// TRaSH's streaming formats need the service name and a WEB source, in
    /// the numbering of the app each was written for. The TV "AMZN" format's
    /// sources are Sonarr's 3 and 4; read as Radarr's numbers they were a
    /// telecine and a workprint, so it could never match an Amazon WEB-DL.
    /// These are the release names the physical rig's fixture serves.
    /// </summary>
    [Theory]
    [InlineData("movies", "b3b3a6ac74ecbd56bcdbefa4799fb9df", "Sintel.2010.1080p.AMZN.WEBRip.DDP5.1.x264-DELUNO", true)]
    [InlineData("movies", "b3b3a6ac74ecbd56bcdbefa4799fb9df", "Sintel.2010.1080p.AMZN.HDTV.x264-DELUNO", false)]
    [InlineData("movies", "b3b3a6ac74ecbd56bcdbefa4799fb9df", "Sintel.2010.1080p.NF.WEB-DL.DDP5.1.H.264-DELUNO", false)]
    [InlineData("movies", "170b1d363bd8516fbf3a3eb05d4faff6", "Sintel.2010.1080p.NF.WEB-DL.DDP5.1.H.264-DELUNO", true)]
    [InlineData("tv", "d660701077794679fd59e8bdf4ce3a29", "Breaking.Bad.S01E03.1080p.AMZN.WEBRip.DDP5.1.x264-DELUNO", true)]
    [InlineData("tv", "d660701077794679fd59e8bdf4ce3a29", "Breaking.Bad.S01E03.1080p.AMZN.WEB-DL.DDP5.1.H.264-DELUNO", true)]
    [InlineData("tv", "d660701077794679fd59e8bdf4ce3a29", "Breaking.Bad.S01E03.1080p.AMZN.HDTV.x264-DELUNO", false)]
    [InlineData("tv", "d34870697c9db575f17700212167be23", "Breaking.Bad.S01E03.1080p.NF.WEB-DL.DDP5.1.H.264-DELUNO", true)]
    [InlineData("tv", "d34870697c9db575f17700212167be23", "Breaking.Bad.S01E03.1080p.AMZN.WEBRip.DDP5.1.x264-DELUNO", false)]
    public void A_genuine_trash_streaming_format_needs_its_service_and_a_web_source(
        string mediaType,
        string trashId,
        string releaseName,
        bool expected)
        => AssertGenuineFormat(mediaType, trashId, releaseName, expected);

    /// <summary>
    /// Sonarr numbers a Blu-ray remux (7) apart from a Blu-ray encode (6), where
    /// Radarr marks a remux with a modifier instead.
    /// </summary>
    [Theory]
    [InlineData("9965a052eb87b0d10313b1cea89eb451", "Harbour.Lights.S01E01.1080p.BluRay.REMUX.AVC.DTS-HD.MA.5.1-FraMeSToR", true)]
    [InlineData("9965a052eb87b0d10313b1cea89eb451", "Harbour.Lights.S01E01.1080p.BluRay.x264-FraMeSToR", false)]
    [InlineData("d6819cba26b1a6508138d25fb5e32293", "Harbour.Lights.S01E01.1080p.BluRay.x264-CtrlHD", true)]
    [InlineData("d6819cba26b1a6508138d25fb5e32293", "Harbour.Lights.S01E01.1080p.BluRay.REMUX.AVC-CtrlHD", false)]
    [InlineData("d6819cba26b1a6508138d25fb5e32293", "Harbour.Lights.S01E01.2160p.BluRay.x265-CtrlHD", false)]
    public void Genuine_trash_tv_group_tiers_tell_a_remux_from_a_blu_ray_encode(
        string trashId,
        string releaseName,
        bool expected)
        => AssertGenuineFormat("tv", trashId, releaseName, expected);

    [Theory]
    [InlineData("tv", true)]
    [InlineData("movies", false)]
    public void The_same_source_number_is_a_web_dl_to_sonarr_and_a_telecine_to_radarr(string mediaType, bool expected)
    {
        const string definition =
            "[{\"implementation\":\"SourceSpecification\",\"required\":true,\"fields\":{\"value\":3}}]";

        Assert.Equal(expected, CustomFormatDefinitionMatcher.Evaluate(
            "Harbour.Lights.S01E01.1080p.WEB-DL.H.264-GRP",
            definition,
            anyCondition: false,
            mediaType).IsMatch);
    }

    private static void AssertGenuineFormat(string mediaType, string trashId, string releaseName, bool expected)
    {
        var source = Assert.Single(
            GuideSourceInventoryCatalog.Current.CustomFormats,
            format => format.TrashId == trashId);
        Assert.Equal(mediaType, source.MediaType);
        var conditions = new JsonArray(source.MatcherClauses
            .Select(clause => (JsonNode)new JsonObject
            {
                ["name"] = clause.Name,
                ["implementation"] = clause.Implementation,
                ["negate"] = clause.Negate,
                ["required"] = clause.Required,
                ["fields"] = JsonNode.Parse(clause.FieldsJson)
            })
            .ToArray()).ToJsonString();
        var now = DateTimeOffset.Parse("2026-09-15T00:00:00Z");
        var format = new CustomFormatItem(trashId, source.Name, mediaType, 0, trashId, conditions, true, now, now);
        var plan = ReleasePreferencePlanFactory.CreateQualityPlan(
            mediaType,
            "WEB 1080p",
            ["WEB 1080p", "Bluray 1080p"],
            customFormats: [format],
            formatIntents: new Dictionary<string, string> { [format.Id] = ProfileFormatIntents.Prefer });

        Assert.True(CustomFormatDefinitionMatcher.CanEvaluate(conditions, mediaType));
        var dryRun = Assert.Single(CustomFormatMatcher.DryRun(releaseName, [format]));
        var typed = Assert.Single(ReleasePreferenceFormatEvidenceFactory.Evaluate(plan, releaseName));
        Assert.Equal(expected, dryRun.IsMatch);
        Assert.Equal(expected, typed.IsMatch);
    }

    [Fact]
    public void Contextual_radarr_language_condition_is_not_claimed_as_release_name_evaluable()
    {
        const string definition =
            "[{\"implementation\":\"LanguageSpecification\",\"required\":true,\"fields\":{\"value\":-2}}]";

        Assert.False(CustomFormatDefinitionMatcher.CanEvaluate(definition));
    }

    /// <summary>
    /// The language condition matches the release name's parsed language tag,
    /// not a raw substring search (#758): a language word fused into an
    /// unrelated token ("LeFrenchKiss") no longer claims a French audio
    /// track, and a release naming more than one language matches a
    /// condition for either.
    /// </summary>
    [Theory]
    [InlineData("Movie.2026.FRENCH.1080p.WEB-DL-GRP", "french", true)]
    [InlineData("LeFrenchKiss.2026.1080p.WEB-DL-GRP", "french", false)]
    [InlineData("Movie.2026.MULTI.FRENCH.1080p.WEB-DL-GRP", "german", false)]
    [InlineData("Movie.2026.MULTI.GERMAN.1080p.WEB-DL-GRP", "multi", true)]
    [InlineData("Movie.2026.MULTI.GERMAN.1080p.WEB-DL-GRP", "german", true)]
    [InlineData("Movie.2026.1080p.WEB-DL-GRP", "french", false)]
    public void Language_condition_matches_the_parsed_language_tag(string releaseName, string language, bool expected)
    {
        var definition = $"[{{\"type\":\"language\",\"value\":\"{language}\"}}]";

        Assert.True(CustomFormatDefinitionMatcher.CanEvaluate(definition));
        Assert.Equal(expected, CustomFormatDefinitionMatcher.Evaluate(releaseName, definition, anyCondition: false).IsMatch);
    }

    private sealed record MatchCase(
        string Id,
        string Conditions,
        string? TrashId,
        string ReleaseName,
        bool Expected);
}
