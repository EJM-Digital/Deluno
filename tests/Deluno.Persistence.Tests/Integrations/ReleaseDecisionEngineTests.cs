using Deluno.Integrations.Search;

namespace Deluno.Persistence.Tests.Integrations;

public sealed class ReleaseDecisionEngineTests
{
    private static ReleaseDecisionInput GoodInput(
        string releaseName = "Movie.2024.1080p.WEB-DL.DDP5.1-GRP",
        string quality = "WEB 1080p",
        string? currentQuality = null,
        string targetQuality = "WEB 1080p",
        long? sizeBytes = 8_000_000_000,
        int? seeders = 25,
        int sourcePriority = 100,
        int customFormatScore = 0,
        IReadOnlyList<string>? neverGrab = null)
        => new(
            ReleaseName: releaseName,
            Quality: quality,
            CurrentQuality: currentQuality,
            TargetQuality: targetQuality,
            SizeBytes: sizeBytes,
            Seeders: seeders,
            DownloadUrl: "https://example.test/file.torrent",
            SourcePriorityScore: sourcePriority,
            CustomFormatScore: customFormatScore,
            NeverGrabPatterns: neverGrab);

    // ── Status ────────────────────────────────────────────────────────────

    [Fact]
    public void Decide_returns_preferred_when_candidate_meets_cutoff()
    {
        var decision = ReleaseDecisionEngine.Decide(GoodInput());

        Assert.Equal("preferred", decision.Status);
        Assert.True(decision.MeetsCutoff);
    }

    [Fact]
    public void Decide_returns_eligible_when_candidate_is_below_cutoff()
    {
        var decision = ReleaseDecisionEngine.Decide(GoodInput(
            releaseName: "Movie.2024.720p.WEB-DL-GRP",
            quality: "WEB 720p",
            targetQuality: "WEB 1080p"));

        Assert.Equal("eligible", decision.Status);
        Assert.False(decision.MeetsCutoff);
    }

    [Fact]
    public void Decide_returns_rejected_for_sample_release()
    {
        var decision = ReleaseDecisionEngine.Decide(GoodInput(
            releaseName: "Movie.2024.1080p.WEB.sample-GRP"));

        Assert.Equal("rejected", decision.Status);
        Assert.True(decision.Score <= -10000);
        Assert.Contains(decision.RiskFlags, r => r.Contains("sample", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_returns_rejected_for_cam_release()
    {
        var decision = ReleaseDecisionEngine.Decide(GoodInput(
            releaseName: "Movie.2024.CAM.x264-GRP"));

        Assert.Equal("rejected", decision.Status);
        Assert.Contains(decision.RiskFlags, r => r.Contains("blocked token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_returns_rejected_for_telesync_release()
    {
        var decision = ReleaseDecisionEngine.Decide(GoodInput(
            releaseName: "Movie.2024.TS.x264-GRP"));

        Assert.Equal("rejected", decision.Status);
    }

    [Fact]
    public void Decide_returns_rejected_for_screener_release()
    {
        var decision = ReleaseDecisionEngine.Decide(GoodInput(
            releaseName: "Movie.2024.SCR.1080p-GRP"));

        Assert.Equal("rejected", decision.Status);
    }

    [Fact]
    public void Decide_returns_rejected_for_never_grab_pattern_match()
    {
        var decision = ReleaseDecisionEngine.Decide(GoodInput(
            releaseName: "Movie.2024.1080p.WEB-DL-BADGROUP",
            neverGrab: ["BADGROUP"]));

        Assert.Equal("rejected", decision.Status);
        Assert.Contains(decision.RiskFlags, r => r.Contains("never-grab", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_never_grab_pattern_is_case_insensitive()
    {
        var decision = ReleaseDecisionEngine.Decide(GoodInput(
            releaseName: "Movie.2024.1080p.WEB-DL-badgroup",
            neverGrab: ["BADGROUP"]));

        Assert.Equal("rejected", decision.Status);
    }

    [Fact]
    public void Decide_returns_risky_when_three_or_more_risk_flags()
    {
        // Three risks: downgrade (-delta), no seeders, too-small size.
        // Target is 2160p so the current 1080p file doesn't meet it — the
        // downgrade becomes a risk flag rather than a hard reject.
        var decision = ReleaseDecisionEngine.Decide(GoodInput(
            releaseName: "Movie.2024.720p.WEB-GRP",
            quality: "WEB 720p",
            currentQuality: "WEB 1080p",
            targetQuality: "WEB 2160p",
            sizeBytes: 100_000,
            seeders: 0));

        Assert.Equal("risky", decision.Status);
        Assert.True(decision.RiskFlags.Count >= 3);
    }

    // ── Quality delta ─────────────────────────────────────────────────────

    [Fact]
    public void Decide_quality_delta_is_positive_for_upgrade()
    {
        var decision = ReleaseDecisionEngine.Decide(GoodInput(
            releaseName: "Movie.2024.1080p.WEB-DL-GRP",
            quality: "WEB 1080p",
            currentQuality: "WEB 720p",
            targetQuality: "WEB 1080p"));

        Assert.True(decision.QualityDelta > 0,
            $"Expected positive delta, got {decision.QualityDelta}");
        Assert.Contains(decision.Reasons, r => r.Contains("improves", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_quality_delta_is_zero_for_same_quality()
    {
        var decision = ReleaseDecisionEngine.Decide(GoodInput(
            quality: "WEB 1080p",
            currentQuality: "WEB 1080p",
            targetQuality: "WEB 1080p"));

        Assert.Equal(0, decision.QualityDelta);
    }

    [Fact]
    public void Decide_quality_delta_is_negative_for_downgrade()
    {
        // Target 2160p so current 1080p doesn't meet cutoff — the downgrade
        // becomes a "below the current file" risk flag, not a hard reject.
        var decision = ReleaseDecisionEngine.Decide(GoodInput(
            releaseName: "Movie.2024.720p.WEB-DL-GRP",
            quality: "WEB 720p",
            currentQuality: "WEB 1080p",
            targetQuality: "WEB 2160p"));

        Assert.True(decision.QualityDelta < 0,
            $"Expected negative delta, got {decision.QualityDelta}");
        Assert.Contains(decision.RiskFlags, r => r.Contains("below the current file", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_quality_delta_equals_candidate_rank_when_no_current_file()
    {
        var decision = ReleaseDecisionEngine.Decide(GoodInput(
            quality: "WEB 1080p",
            currentQuality: null));

        var expectedRank = ReleaseDecisionEngine.QualityRank("WEB 1080p");
        Assert.Equal(expectedRank, decision.QualityDelta);
    }

    // ── Seeder scoring ────────────────────────────────────────────────────

    [Fact]
    public void Decide_adds_risk_flag_when_no_seeders_reported()
    {
        var decision = ReleaseDecisionEngine.Decide(GoodInput(seeders: null));

        Assert.Contains(decision.RiskFlags, r => r.Contains("seeders", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(-40, decision.SeederScore);
    }

    [Fact]
    public void Decide_adds_risk_flag_when_zero_seeders()
    {
        var decision = ReleaseDecisionEngine.Decide(GoodInput(seeders: 0));

        Assert.Contains(decision.RiskFlags, r => r.Contains("No seeders", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(-160, decision.SeederScore);
    }

    [Fact]
    public void Decide_adds_risk_flag_for_very_low_seed_count()
    {
        var decision = ReleaseDecisionEngine.Decide(GoodInput(seeders: 2));

        Assert.Contains(decision.RiskFlags, r => r.Contains("low seed", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(-70, decision.SeederScore);
    }

    [Fact]
    public void Decide_seeder_score_is_capped_at_220()
    {
        var decision = ReleaseDecisionEngine.Decide(GoodInput(seeders: 1000));

        Assert.Equal(220, decision.SeederScore);
    }

    // ── Size scoring ──────────────────────────────────────────────────────

    [Fact]
    public void Decide_adds_risk_flag_when_size_not_reported()
    {
        var decision = ReleaseDecisionEngine.Decide(GoodInput(sizeBytes: 0));

        Assert.Contains(decision.RiskFlags, r => r.Contains("size", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(-50, decision.SizeScore);
    }

    [Fact]
    public void Decide_adds_risk_flag_when_size_unusually_small_for_1080p()
    {
        var decision = ReleaseDecisionEngine.Decide(GoodInput(
            quality: "WEB 1080p",
            sizeBytes: 200_000_000)); // 0.2 GB, too small for 1080p

        Assert.Contains(decision.RiskFlags, r => r.Contains("small", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(-180, decision.SizeScore);
    }

    [Fact]
    public void Decide_adds_risk_flag_when_size_unusually_large_for_1080p()
    {
        var decision = ReleaseDecisionEngine.Decide(GoodInput(
            quality: "WEB 1080p",
            sizeBytes: 40_000_000_000L)); // 40 GB, too large for 1080p WEB

        Assert.Contains(decision.RiskFlags, r => r.Contains("large", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(-80, decision.SizeScore);
    }

    [Fact]
    public void Decide_size_score_is_positive_when_within_expected_range()
    {
        var decision = ReleaseDecisionEngine.Decide(GoodInput(
            quality: "WEB 1080p",
            sizeBytes: 8_000_000_000L)); // 8 GB, good for 1080p

        Assert.Equal(80, decision.SizeScore);
    }

    [Fact]
    public void Decide_reports_estimated_bitrate_from_size()
    {
        var decision = ReleaseDecisionEngine.Decide(GoodInput(sizeBytes: 8_000_000_000L));

        Assert.NotNull(decision.EstimatedBitrateMbps);
        Assert.True(decision.EstimatedBitrateMbps > 0);
    }

    // ── Codec & HDR bonuses ───────────────────────────────────────────────

    [Fact]
    public void Decide_detects_x265_codec()
    {
        var decision = ReleaseDecisionEngine.Decide(GoodInput(
            releaseName: "Movie.2024.1080p.WEB-DL.x265-GRP"));

        Assert.Contains(decision.Reasons, r => r.Contains("HEVC", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_detects_hevc_codec()
    {
        var decision = ReleaseDecisionEngine.Decide(GoodInput(
            releaseName: "Movie.2024.1080p.WEB-DL.HEVC-GRP"));

        Assert.Contains(decision.Reasons, r => r.Contains("HEVC", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_detects_hdr10_signal()
    {
        var decision = ReleaseDecisionEngine.Decide(GoodInput(
            releaseName: "Movie.2024.2160p.BluRay.HDR10.x265-GRP",
            quality: "Bluray 2160p",
            sizeBytes: 30_000_000_000L));

        Assert.Contains(decision.Reasons, r => r.Contains("HDR", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_penalises_hardcoded_subtitles()
    {
        var decision = ReleaseDecisionEngine.Decide(GoodInput(
            releaseName: "Movie.2024.1080p.WEB.HC.sub-GRP"));

        Assert.Contains(decision.RiskFlags, r => r.Contains("Hardcoded", StringComparison.OrdinalIgnoreCase));
    }

    // ── Release group inference ───────────────────────────────────────────

    [Fact]
    public void Decide_infers_release_group_from_name()
    {
        var decision = ReleaseDecisionEngine.Decide(GoodInput(
            releaseName: "Movie.2024.1080p.WEB-DL-MYGROUP"));

        Assert.Equal("MYGROUP", decision.ReleaseGroup);
        Assert.Contains(decision.Reasons, r => r.Contains("MYGROUP"));
    }

    [Fact]
    public void Decide_release_group_is_null_when_not_detected()
    {
        // No trailing -WORD pattern, so no group can be inferred
        var decision = ReleaseDecisionEngine.Decide(GoodInput(
            releaseName: "Movie.2024.1080p.WEB"));

        Assert.Null(decision.ReleaseGroup);
    }

    // ── Score components ──────────────────────────────────────────────────

    [Fact]
    public void Decide_custom_format_score_is_added_to_total()
    {
        var without = ReleaseDecisionEngine.Decide(GoodInput(customFormatScore: 0));
        var with250 = ReleaseDecisionEngine.Decide(GoodInput(customFormatScore: 250));

        Assert.Equal(250, with250.Score - without.Score);
        Assert.Equal(250, with250.CustomFormatScore);
    }

    [Fact]
    public void Decide_score_is_very_negative_for_rejected_candidate()
    {
        var decision = ReleaseDecisionEngine.Decide(GoodInput(
            releaseName: "Movie.2024.CAM.x264-GRP"));

        Assert.True(decision.Score <= -10000);
    }

    [Fact]
    public void Decide_score_below_cutoff_subtracts_250()
    {
        var meetsDecision = ReleaseDecisionEngine.Decide(GoodInput(
            quality: "WEB 1080p", targetQuality: "WEB 1080p"));
        var belowDecision = ReleaseDecisionEngine.Decide(GoodInput(
            releaseName: "Movie.2024.720p.WEB-DL-GRP",
            quality: "WEB 720p",
            targetQuality: "WEB 1080p",
            sizeBytes: 2_000_000_000L));

        Assert.True(meetsDecision.Score > belowDecision.Score + 200);
    }

    // ── Policy version ────────────────────────────────────────────────────

    [Fact]
    public void Decide_includes_current_policy_version()
    {
        var decision = ReleaseDecisionEngine.Decide(GoodInput());

        Assert.False(string.IsNullOrWhiteSpace(decision.PolicyVersion));
        Assert.Equal(Deluno.Platform.Quality.MediaPolicyCatalog.CurrentVersion, decision.PolicyVersion);
    }

    // ── Summary ───────────────────────────────────────────────────────────

    [Fact]
    public void Decide_summary_mentions_status()
    {
        var preferred = ReleaseDecisionEngine.Decide(GoodInput());
        var rejected = ReleaseDecisionEngine.Decide(GoodInput(
            releaseName: "Movie.2024.CAM-GRP"));

        Assert.Contains("Preferred", preferred.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Rejected", rejected.Summary, StringComparison.OrdinalIgnoreCase);
    }

    // ── Missing URL risk ─────────────────────────────────────────────────

    [Fact]
    public void Decide_adds_risk_flag_when_download_url_is_empty()
    {
        var input = new ReleaseDecisionInput(
            ReleaseName: "Movie.2024.1080p.WEB-DL-GRP",
            Quality: "WEB 1080p",
            CurrentQuality: null,
            TargetQuality: "WEB 1080p",
            SizeBytes: 8_000_000_000L,
            Seeders: 25,
            DownloadUrl: "",
            SourcePriorityScore: 100,
            CustomFormatScore: 0);

        var decision = ReleaseDecisionEngine.Decide(input);

        Assert.Contains(decision.RiskFlags, r => r.Contains("URL", StringComparison.OrdinalIgnoreCase));
    }
}
