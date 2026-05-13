using Deluno.Integrations.Search;
using Deluno.Platform.Contracts;

namespace Deluno.Persistence.Tests.Integrations;

public sealed class CustomFormatMatcherTests
{
    private static CustomFormatItem Format(string id, string name, int score, string? conditions)
        => new(id, name, "movies", score, null, conditions ?? string.Empty, false,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    // ── No formats / empty ────────────────────────────────────────────────

    [Fact]
    public void Evaluate_returns_zero_score_when_no_formats_supplied()
    {
        var total = CustomFormatMatcher.Evaluate("Movie.2024.1080p.WEB-DL-GRP", null, out var matched);

        Assert.Equal(0, total);
        Assert.Empty(matched);
    }

    [Fact]
    public void Evaluate_returns_zero_score_when_format_list_is_empty()
    {
        var total = CustomFormatMatcher.Evaluate("Movie.2024.1080p.WEB-DL-GRP", [], out var matched);

        Assert.Equal(0, total);
        Assert.Empty(matched);
    }

    // ── Legacy bare-token conditions ──────────────────────────────────────

    [Fact]
    public void Evaluate_matches_legacy_bare_token_as_release_title_substring()
    {
        var formats = new[] { Format("f1", "WEB-DL", 100, "WEB-DL") };

        var total = CustomFormatMatcher.Evaluate("Movie.2024.1080p.WEB-DL-GRP", formats, out var matched);

        Assert.Equal(100, total);
        Assert.Single(matched);
        Assert.Equal("f1", matched[0].FormatId);
    }

    [Fact]
    public void Evaluate_does_not_match_when_bare_token_absent()
    {
        var formats = new[] { Format("f1", "BluRay", 200, "BluRay") };

        var total = CustomFormatMatcher.Evaluate("Movie.2024.1080p.WEB-DL-GRP", formats, out var matched);

        Assert.Equal(0, total);
        Assert.Empty(matched);
    }

    // ── Legacy type:value conditions ──────────────────────────────────────

    [Fact]
    public void Evaluate_matches_legacy_type_colon_value_condition()
    {
        var formats = new[] { Format("f1", "1080p", 50, "resolution:1080p") };

        var total = CustomFormatMatcher.Evaluate("Movie.2024.1080p.WEB-DL-GRP", formats, out var matched);

        Assert.Equal(50, total);
        Assert.Single(matched);
    }

    // ── JSON array conditions: releaseTitle ───────────────────────────────

    [Fact]
    public void Evaluate_matches_json_release_title_substring()
    {
        var conditions = """[{"type":"releaseTitle","value":"WEB-DL"}]""";
        var formats = new[] { Format("f1", "WEB-DL Format", 75, conditions) };

        var total = CustomFormatMatcher.Evaluate("Movie.2024.1080p.WEB-DL.DDP5.1-GRP", formats, out var matched);

        Assert.Equal(75, total);
        Assert.Single(matched);
    }

    [Fact]
    public void Evaluate_matches_release_title_via_regex()
    {
        var conditions = """[{"type":"releaseTitle","value":"WEB[-.]DL"}]""";
        var formats = new[] { Format("f1", "WEB-DL Regex", 80, conditions) };

        var total = CustomFormatMatcher.Evaluate("Movie.2024.1080p.WEB-DL-GRP", formats, out var matched);

        Assert.Equal(80, total);
        Assert.Single(matched);
    }

    // ── JSON array conditions: source ─────────────────────────────────────

    [Fact]
    public void Evaluate_matches_bluray_source()
    {
        var conditions = """[{"type":"source","value":"BluRay"}]""";
        var formats = new[] { Format("f1", "BluRay", 100, conditions) };

        var total = CustomFormatMatcher.Evaluate("Movie.2024.1080p.BluRay.x265-GRP", formats, out _);

        Assert.Equal(100, total);
    }

    [Fact]
    public void Evaluate_matches_webrip_source()
    {
        var conditions = """[{"type":"source","value":"WEBRip"}]""";
        var formats = new[] { Format("f1", "WEBRip", 30, conditions) };

        var total = CustomFormatMatcher.Evaluate("Movie.2024.1080p.WEBRip.x264-GRP", formats, out _);

        Assert.Equal(30, total);
    }

    [Fact]
    public void Evaluate_does_not_match_web_source_against_webrip_release()
    {
        var conditions = """[{"type":"source","value":"WEB"}]""";
        var formats = new[] { Format("f1", "WEB", 50, conditions) };

        // WEBRip should not match plain WEB
        var total = CustomFormatMatcher.Evaluate("Movie.2024.1080p.WEBRip.x264-GRP", formats, out var matched);

        Assert.Equal(0, total);
        Assert.Empty(matched);
    }

    // ── JSON array conditions: resolution ─────────────────────────────────

    [Fact]
    public void Evaluate_matches_1080p_resolution()
    {
        var conditions = """[{"type":"resolution","value":"1080p"}]""";
        var formats = new[] { Format("f1", "1080p", 10, conditions) };

        var total = CustomFormatMatcher.Evaluate("Movie.2024.1080p.WEB-DL-GRP", formats, out _);

        Assert.Equal(10, total);
    }

    [Fact]
    public void Evaluate_matches_4k_as_2160p()
    {
        var conditions = """[{"type":"resolution","value":"2160p"}]""";
        var formats = new[] { Format("f1", "4K", 50, conditions) };

        var total = CustomFormatMatcher.Evaluate("Movie.2024.4K.BluRay.REMUX-GRP", formats, out _);

        Assert.Equal(50, total);
    }

    // ── JSON array conditions: hdr ────────────────────────────────────────

    [Fact]
    public void Evaluate_matches_dv_hdr_signal()
    {
        var conditions = """[{"type":"hdr","value":"DV"}]""";
        var formats = new[] { Format("f1", "Dolby Vision", 150, conditions) };

        var total = CustomFormatMatcher.Evaluate("Movie.2024.2160p.BluRay.DV.HDR10.HEVC-GRP", formats, out _);

        Assert.Equal(150, total);
    }

    [Fact]
    public void Evaluate_matches_hdr10_signal()
    {
        var conditions = """[{"type":"hdr","value":"HDR10"}]""";
        var formats = new[] { Format("f1", "HDR10", 100, conditions) };

        var total = CustomFormatMatcher.Evaluate("Movie.2024.2160p.BluRay.HDR10.HEVC-GRP", formats, out _);

        Assert.Equal(100, total);
    }

    // ── JSON array conditions: codec ──────────────────────────────────────

    [Fact]
    public void Evaluate_matches_x265_codec()
    {
        var conditions = """[{"type":"codec","value":"x265"}]""";
        var formats = new[] { Format("f1", "x265/HEVC", 25, conditions) };

        var total = CustomFormatMatcher.Evaluate("Movie.2024.1080p.WEB-DL.x265-GRP", formats, out _);

        Assert.Equal(25, total);
    }

    [Fact]
    public void Evaluate_matches_hevc_alias_for_x265()
    {
        var conditions = """[{"type":"codec","value":"HEVC"}]""";
        var formats = new[] { Format("f1", "HEVC", 25, conditions) };

        var total = CustomFormatMatcher.Evaluate("Movie.2024.1080p.WEB-DL.x265-GRP", formats, out _);

        Assert.Equal(25, total);
    }

    [Fact]
    public void Evaluate_matches_av1_codec()
    {
        var conditions = """[{"type":"codec","value":"AV1"}]""";
        var formats = new[] { Format("f1", "AV1", 15, conditions) };

        var total = CustomFormatMatcher.Evaluate("Movie.2024.1080p.WEB-DL.AV1-GRP", formats, out _);

        Assert.Equal(15, total);
    }

    // ── JSON array conditions: releaseGroup ───────────────────────────────

    [Fact]
    public void Evaluate_matches_release_group()
    {
        var conditions = """[{"type":"releaseGroup","value":"YIFY"}]""";
        var formats = new[] { Format("f1", "YIFY Group", -100, conditions) };

        var total = CustomFormatMatcher.Evaluate("Movie.2024.1080p.WEB-DL-YIFY", formats, out _);

        Assert.Equal(-100, total);
    }

    [Fact]
    public void Evaluate_release_group_match_is_case_insensitive()
    {
        var conditions = """[{"type":"releaseGroup","value":"yify"}]""";
        var formats = new[] { Format("f1", "YIFY Group", -100, conditions) };

        var total = CustomFormatMatcher.Evaluate("Movie.2024.1080p.WEB-DL-YIFY", formats, out _);

        Assert.Equal(-100, total);
    }

    // ── Multi-condition AND logic ─────────────────────────────────────────

    [Fact]
    public void Evaluate_requires_all_conditions_to_match()
    {
        var conditions = """[{"type":"resolution","value":"1080p"},{"type":"source","value":"BluRay"}]""";
        var formats = new[] { Format("f1", "1080p BluRay", 200, conditions) };

        // Has 1080p but not BluRay
        var miss = CustomFormatMatcher.Evaluate("Movie.2024.1080p.WEB-DL-GRP", formats, out _);
        // Has both
        var hit = CustomFormatMatcher.Evaluate("Movie.2024.1080p.BluRay.x264-GRP", formats, out _);

        Assert.Equal(0, miss);
        Assert.Equal(200, hit);
    }

    // ── Negated conditions ────────────────────────────────────────────────

    [Fact]
    public void Evaluate_negated_condition_matches_when_token_absent()
    {
        var conditions = """[{"type":"releaseTitle","value":"WEBRip","negate":true}]""";
        var formats = new[] { Format("f1", "Not WEBRip", 50, conditions) };

        var hit = CustomFormatMatcher.Evaluate("Movie.2024.1080p.WEB-DL-GRP", formats, out _);
        var miss = CustomFormatMatcher.Evaluate("Movie.2024.1080p.WEBRip.x264-GRP", formats, out _);

        Assert.Equal(50, hit);
        Assert.Equal(0, miss);
    }

    // ── Score accumulation ────────────────────────────────────────────────

    [Fact]
    public void Evaluate_accumulates_scores_across_multiple_matching_formats()
    {
        var formats = new CustomFormatItem[]
        {
            Format("f1", "1080p",    10, """[{"type":"resolution","value":"1080p"}]"""),
            Format("f2", "x265",     25, """[{"type":"codec","value":"x265"}]"""),
            Format("f3", "NoMatch", 999, """[{"type":"source","value":"BluRay"}]""")
        };

        var total = CustomFormatMatcher.Evaluate(
            "Movie.2024.1080p.WEB-DL.x265-GRP", formats, out var matched);

        Assert.Equal(35, total);
        Assert.Equal(2, matched.Count);
        Assert.Contains(matched, m => m.FormatId == "f1");
        Assert.Contains(matched, m => m.FormatId == "f2");
    }

    [Fact]
    public void Evaluate_allows_negative_custom_format_scores()
    {
        var formats = new[] { Format("f1", "Penalty", -200, "WEBRip") };

        var total = CustomFormatMatcher.Evaluate("Movie.2024.1080p.WEBRip.x264-GRP", formats, out _);

        Assert.Equal(-200, total);
    }

    // ── DryRun ────────────────────────────────────────────────────────────

    [Fact]
    public void DryRun_returns_entry_for_every_format_regardless_of_match()
    {
        var formats = new CustomFormatItem[]
        {
            Format("f1", "Hit",  100, """[{"type":"resolution","value":"1080p"}]"""),
            Format("f2", "Miss", 200, """[{"type":"source","value":"BluRay"}]""")
        };

        var results = CustomFormatMatcher.DryRun("Movie.2024.1080p.WEB-DL-GRP", formats);

        Assert.Equal(2, results.Count);
        Assert.True(results.Single(r => r.FormatId == "f1").IsMatch);
        Assert.False(results.Single(r => r.FormatId == "f2").IsMatch);
    }

    [Fact]
    public void DryRun_includes_matched_and_missed_condition_labels()
    {
        var conditions = """[{"type":"resolution","value":"1080p"},{"type":"source","value":"BluRay"}]""";
        var formats = new[] { Format("f1", "Test", 100, conditions) };

        var results = CustomFormatMatcher.DryRun("Movie.2024.1080p.WEB-DL-GRP", formats);

        var result = results[0];
        Assert.False(result.IsMatch);
        Assert.Single(result.MatchedConditions);
        Assert.Single(result.MissedConditions);
        Assert.Contains("resolution:1080p", result.MatchedConditions);
        Assert.Contains("source:BluRay", result.MissedConditions);
    }

    [Fact]
    public void DryRun_format_with_no_conditions_never_matches()
    {
        var formats = new[] { Format("f1", "Empty", 100, null) };

        var results = CustomFormatMatcher.DryRun("Movie.2024.1080p.WEB-DL-GRP", formats);

        Assert.Single(results);
        Assert.False(results[0].IsMatch);
        Assert.Contains("No conditions defined", results[0].MissedConditions);
    }

    // ── Match result shape ────────────────────────────────────────────────

    [Fact]
    public void Evaluate_matched_result_contains_format_metadata()
    {
        var formats = new[] { Format("my-id", "My Format", 77, "WEB-DL") };

        CustomFormatMatcher.Evaluate("Movie.2024.1080p.WEB-DL-GRP", formats, out var matched);

        var result = matched[0];
        Assert.Equal("my-id", result.FormatId);
        Assert.Equal("My Format", result.FormatName);
        Assert.Equal(77, result.Score);
    }
}
