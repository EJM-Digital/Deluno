using Deluno.Integrations.Search;
using Deluno.Quality.Contracts;

namespace Deluno.Integrations.Tests.Search;

public sealed class CustomFormatMatcherTests
{
    [Fact]
    public void DryRun_strips_the_persisted_regex_marker_before_matching()
    {
        var formats = new[]
        {
            new CustomFormatItem(
                Id: "web-tier-01",
                Name: "WEB Tier 01",
                MediaType: "movies",
                Score: 1700,
                TrashId: "trash-web-tier-01",
                Conditions: """[{"type":"releaseTitle","value":"regex: \\b(NTb|NTG|FLUX)\\b","required":true}]""",
                UpgradeAllowed: true,
                CreatedUtc: DateTimeOffset.UtcNow,
                UpdatedUtc: DateTimeOffset.UtcNow)
        };

        var result = CustomFormatMatcher.DryRun(
            "Big.Buck.Bunny.2008.2160p.WEB-DL.x265-NTb",
            formats);

        var match = Assert.Single(result);
        Assert.True(match.IsMatch);
        Assert.Equal(new[] { "releaseTitle:regex: \\b(NTb|NTG|FLUX)\\b" }, match.MatchedConditions);
    }

    [Fact]
    public void Plain_release_title_conditions_keep_substring_matching()
    {
        var formats = new[]
        {
            new CustomFormatItem(
                Id: "web-dl",
                Name: "WEB-DL bonus",
                MediaType: "movies",
                Score: 100,
                TrashId: null,
                Conditions: "[{\"type\":\"releaseTitle\",\"value\":\"WEB-DL\",\"required\":true}]",
                UpgradeAllowed: true,
                CreatedUtc: DateTimeOffset.UtcNow,
                UpdatedUtc: DateTimeOffset.UtcNow)
        };

        var result = CustomFormatMatcher.Evaluate(
            "Big.Buck.Bunny.2008.1080p.WEB-DL.x264-DELUNO",
            formats,
            out var matched);

        Assert.Equal(100, result);
        Assert.Single(matched);
    }

    [Fact]
    public void Legacy_regex_conditions_match_guide_backed_release_groups()
    {
        var formats = new[]
        {
            new CustomFormatItem(
                Id: "legacy-web-tier-01",
                Name: "Legacy WEB Tier 01",
                MediaType: "movies",
                Score: 1700,
                TrashId: "trash-web-tier-01",
                Conditions: """regex: \b(NTb|NTG|FLUX)\b""",
                UpgradeAllowed: true,
                CreatedUtc: DateTimeOffset.UtcNow,
                UpdatedUtc: DateTimeOffset.UtcNow)
        };

        var result = CustomFormatMatcher.Evaluate(
            "Big.Buck.Bunny.2008.2160p.WEB-DL.x265-NTb",
            formats,
            out var matched);

        Assert.Equal(1700, result);
        Assert.Single(matched);
    }

    [Fact]
    public void Guide_backed_patterns_are_alternatives_not_required_all_at_once()
    {
        var formats = new[]
        {
            new CustomFormatItem(
                Id: "hdr",
                Name: "HDR",
                MediaType: "movies",
                Score: 500,
                TrashId: "trash-hdr",
                Conditions: """[{"type":"releaseTitle","value":"regex: \\bHDR(\\b|\\d)\\b","required":true},{"type":"releaseTitle","value":"regex: \\bHDR10\\b","required":true},{"type":"releaseTitle","value":"regex: \\bHLG\\b","required":true}]""",
                UpgradeAllowed: true,
                CreatedUtc: DateTimeOffset.UtcNow,
                UpdatedUtc: DateTimeOffset.UtcNow)
        };

        var result = CustomFormatMatcher.Evaluate(
            "Big.Buck.Bunny.2008.2160p.WEB-DL.HDR.x265-DELUNO",
            formats,
            out var matched);

        Assert.Equal(500, result);
        Assert.Single(matched);
        Assert.Contains("releaseTitle:regex: \\bHDR(\\b|\\d)\\b", matched[0].MatchedConditions);
    }

    [Fact]
    public void Custom_rule_criteria_remain_all_required()
    {
        var formats = new[]
        {
            new CustomFormatItem(
                Id: "hdr-and-web",
                Name: "HDR WEB",
                MediaType: "movies",
                Score: 100,
                TrashId: null,
                Conditions: """[{"type":"releaseTitle","value":"HDR","required":true},{"type":"releaseTitle","value":"WEB-DL","required":true}]""",
                UpgradeAllowed: true,
                CreatedUtc: DateTimeOffset.UtcNow,
                UpdatedUtc: DateTimeOffset.UtcNow)
        };

        var result = CustomFormatMatcher.Evaluate(
            "Big.Buck.Bunny.2008.2160p.WEB-DL.x265-DELUNO",
            formats,
            out var matched);

        Assert.Equal(0, result);
        Assert.Empty(matched);
    }

    [Fact]
    public void Optional_custom_conditions_do_not_veto_a_required_match()
    {
        var formats = new[]
        {
            new CustomFormatItem(
                Id: "hdr-with-optional-source",
                Name: "HDR with optional source",
                MediaType: "movies",
                Score: 100,
                TrashId: null,
                Conditions: """[{"type":"releaseTitle","value":"HDR","required":true},{"type":"source","value":"REMUX","required":false}]""",
                UpgradeAllowed: true,
                CreatedUtc: DateTimeOffset.UtcNow,
                UpdatedUtc: DateTimeOffset.UtcNow)
        };

        var requiredOnly = CustomFormatMatcher.Evaluate(
            "Movie.2024.1080p.WEB-DL.HDR.x264-GRP",
            formats,
            out var matchedRequiredOnly);
        var optionalOnly = CustomFormatMatcher.Evaluate(
            "Movie.2024.1080p.REMUX.HDR.x264-GRP",
            formats,
            out var matchedWithOptional);

        Assert.Equal(100, requiredOnly);
        Assert.Single(matchedRequiredOnly);
        Assert.Equal(100, optionalOnly);
        Assert.Contains(matchedWithOptional[0].MatchedConditions, condition => condition.Contains("REMUX", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Optional_only_custom_rule_needs_an_actual_match()
    {
        var formats = new[]
        {
            new CustomFormatItem(
                Id: "optional-only",
                Name: "Optional only",
                MediaType: "movies",
                Score: 100,
                TrashId: null,
                Conditions: """[{"type":"releaseTitle","value":"HDR","required":false}]""",
                UpgradeAllowed: true,
                CreatedUtc: DateTimeOffset.UtcNow,
                UpdatedUtc: DateTimeOffset.UtcNow)
        };

        var score = CustomFormatMatcher.Evaluate(
            "Movie.2024.1080p.WEB-DL.x264-GRP",
            formats,
            out var matched);

        Assert.Equal(0, score);
        Assert.Empty(matched);
    }

    [Fact]
    public void Informational_formats_are_reported_but_do_not_drive_upgrade_score()
    {
        var formats = new[]
        {
            new CustomFormatItem(
                Id: "informational-hdr",
                Name: "Informational HDR",
                MediaType: "movies",
                Score: 900,
                TrashId: null,
                Conditions: "[{\"type\":\"releaseTitle\",\"value\":\"HDR\",\"required\":true}]",
                UpgradeAllowed: false,
                CreatedUtc: DateTimeOffset.UtcNow,
                UpdatedUtc: DateTimeOffset.UtcNow),
            new CustomFormatItem(
                Id: "upgrade-web",
                Name: "Upgrade WEB",
                MediaType: "movies",
                Score: 100,
                TrashId: null,
                Conditions: "[{\"type\":\"releaseTitle\",\"value\":\"WEB-DL\",\"required\":true}]",
                UpgradeAllowed: true,
                CreatedUtc: DateTimeOffset.UtcNow,
                UpdatedUtc: DateTimeOffset.UtcNow)
        };

        var upgradeScore = CustomFormatMatcher.EvaluateUpgradeScore(
            "Movie.2024.1080p.WEB-DL.HDR.x264-GRP",
            formats,
            out var matched);

        Assert.Equal(100, upgradeScore);
        Assert.Equal(2, matched.Count);
        Assert.Contains(matched, item => item.FormatName == "Informational HDR" && !item.UpgradeAllowed);
    }
}
