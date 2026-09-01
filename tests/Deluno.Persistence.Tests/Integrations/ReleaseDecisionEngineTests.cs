using Deluno.Integrations.Search;
using Deluno.Platform.Contracts;
using Deluno.Quality.ReleasePreferences;

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
        IReadOnlyList<string>? neverGrab = null,
        IReadOnlyList<string>? allowedQualities = null,
        IReadOnlyList<ReleaseProfileItem>? releaseProfiles = null,
        string? indexerProtocol = null,
        double? releaseAgeHours = null,
        int? minimumAgeMinutes = null,
        int? retentionDays = null,
        int? maximumSizeMb = null,
        string? indexerFlags = null,
        string? preferIndexerFlags = null)
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
            NeverGrabPatterns: neverGrab,
            AllowedQualities: allowedQualities,
            ReleaseProfiles: releaseProfiles,
            IndexerProtocol: indexerProtocol,
            ReleaseAgeHours: releaseAgeHours,
            MinimumAgeMinutes: minimumAgeMinutes,
            RetentionDays: retentionDays,
            MaximumSizeMb: maximumSizeMb,
            IndexerFlags: indexerFlags,
            PreferIndexerFlags: preferIndexerFlags);

    // ── Allowed qualities (#283) ──────────────────────────────────────────

    [Fact]
    public void Decide_rejects_a_quality_the_profile_does_not_allow()
    {
        // Live regression: the shipped "Standard Movies" profile allows up to
        // Bluray 1080p, and Deluno grabbed WEB 2160p and called it preferred,
        // because only the cutoff reached the engine.
        var decision = ReleaseDecisionEngine.Decide(GoodInput(
            releaseName: "Movie.2024.2160p.WEB-DL.x265-GRP",
            quality: "WEB 2160p",
            targetQuality: "WEB 1080p",
            sizeBytes: 20_000_000_000,
            allowedQualities: ["WEB 720p", "WEB 1080p", "Bluray 1080p"]));

        Assert.Equal("rejected", decision.Status);
        Assert.Contains(decision.Reasons.Concat(decision.RiskFlags), text => text.Contains("not one of the qualities this profile allows", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_accepts_a_quality_inside_the_allowed_list()
    {
        var decision = ReleaseDecisionEngine.Decide(GoodInput(
            allowedQualities: ["WEB 720p", "WEB 1080p", "Bluray 1080p"]));

        Assert.Equal("preferred", decision.Status);
    }

    [Fact]
    public void Decide_accepts_an_allowed_quality_that_sits_below_cutoff()
    {
        // Below cutoff is "eligible", not rejected — the allowed list governs
        // which tiers may be grabbed, the cutoff governs how far to upgrade.
        var decision = ReleaseDecisionEngine.Decide(GoodInput(
            releaseName: "Movie.2024.720p.WEB-DL-GRP",
            quality: "WEB 720p",
            targetQuality: "WEB 1080p",
            sizeBytes: 4_000_000_000,
            allowedQualities: ["WEB 720p", "WEB 1080p"]));

        Assert.Equal("eligible", decision.Status);
    }

    [Fact]
    public void Decide_treats_an_empty_allowed_list_as_unconstrained()
    {
        // Empty must mean "the cutoff decides", never "nothing is allowed".
        var decision = ReleaseDecisionEngine.Decide(GoodInput(allowedQualities: []));

        Assert.True(decision.Status == "preferred", $"{decision.Status}: {decision.Summary} | {string.Join("; ", decision.Reasons)} | {string.Join("; ", decision.RiskFlags)}");
    }

    // ── Size rules (#284) ─────────────────────────────────────────────────

    [Fact]
    public void Decide_does_not_reject_when_the_indexer_omits_the_size()
    {
        // An unreported size is not a size violation; rejecting here would block
        // every indexer that omits the field.
        var decision = ReleaseDecisionEngine.Decide(GoodInput(sizeBytes: null));

        Assert.NotEqual("rejected", decision.Status);
    }

    // ── Status ────────────────────────────────────────────────────────────

    [Fact]
    public void Decide_returns_preferred_when_candidate_meets_cutoff()
    {
        var decision = ReleaseDecisionEngine.Decide(GoodInput());

        Assert.Equal("preferred", decision.Status);
        Assert.True(decision.MeetsCutoff);
    }

    [Fact]
    public void Typed_decisions_do_not_publish_legacy_numeric_explanations()
    {
        var decision = ReleaseDecisionEngine.Decide(new ReleaseDecisionInput(
            ReleaseName: "Movie.2024.1080p.WEB-DL.x265-GRP",
            Quality: "WEB 1080p",
            CurrentQuality: null,
            TargetQuality: "WEB 1080p",
            SizeBytes: 2_000_000_000,
            Seeders: 20,
            DownloadUrl: "https://example.test/release",
            SourcePriorityScore: 100,
            CustomFormatScore: 900,
            IndexerProtocol: "newznab",
            IndexerFlags: "free",
            PreferIndexerFlags: "free",
            ReleaseProfiles: [Profile(
                preferredProtocol: "usenet",
                preferredTerms: [new ReleaseTermScore("x265", 150)])],
            PreferencePlan: SnapshotPlan()));

        Assert.Equal(0, decision.Score);
        Assert.DoesNotContain(decision.Reasons, reason =>
            reason.Contains("points", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("(+", StringComparison.Ordinal));
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
        // Three risks: downgrade (-delta), no seeders, unreported size.
        // Target is 2160p so the current 1080p file doesn't meet it — the
        // downgrade becomes a risk flag rather than a hard reject.
        //
        // The size here is null rather than tiny on purpose: a size *below the
        // configured minimum* is now a hard rejection (#284), so using one would
        // test rejection instead of escalation. An unreported size stays a plain
        // risk, which is what this test is about.
        var decision = ReleaseDecisionEngine.Decide(GoodInput(
            releaseName: "Movie.2024.720p.WEB-GRP",
            quality: "WEB 720p",
            currentQuality: "WEB 1080p",
            targetQuality: "WEB 2160p",
            sizeBytes: null,
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

    [Fact]
    public void Decide_blocks_an_equivalent_same_quality_candidate_when_installed_formats_are_known()
    {
        var decision = ReleaseDecisionEngine.Decide(GoodInput(
            currentQuality: "WEB 1080p",
            targetQuality: "WEB 1080p",
            customFormatScore: 100) with
        {
            CurrentCustomFormatScore = 100
        });

        Assert.Equal("rejected", decision.Status);
        Assert.Contains(decision.RiskFlags, risk => risk.Contains("Equivalent replacement", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_allows_a_same_quality_candidate_when_it_improves_installed_formats()
    {
        var decision = ReleaseDecisionEngine.Decide(GoodInput(
            currentQuality: "WEB 1080p",
            targetQuality: "WEB 1080p",
            customFormatScore: 125) with
        {
            CurrentCustomFormatScore = 100
        });

        Assert.Equal("preferred", decision.Status);
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
    public void Decide_rejects_when_size_is_below_the_minimum_for_1080p()
    {
        var decision = ReleaseDecisionEngine.Decide(GoodInput(
            quality: "WEB 1080p",
            sizeBytes: 200_000_000)); // 0.2 GB, below the configured floor

        // Size Rules is described in the UI as the final check that *rejects*.
        // It used to only subtract score, so a release three orders of magnitude
        // under the floor was still grabbed (#284).
        Assert.Equal("rejected", decision.Status);
        Assert.Contains(decision.RiskFlags, r => r.Contains("below", StringComparison.OrdinalIgnoreCase) && r.Contains("minimum", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(-180, decision.SizeScore);
    }

    [Fact]
    public void Decide_rejects_when_size_is_above_the_maximum_for_1080p()
    {
        var decision = ReleaseDecisionEngine.Decide(GoodInput(
            quality: "WEB 1080p",
            sizeBytes: 40_000_000_000L)); // 40 GB, above the configured ceiling

        Assert.Equal("rejected", decision.Status);
        Assert.Contains(decision.RiskFlags, r => r.Contains("above", StringComparison.OrdinalIgnoreCase) && r.Contains("maximum", StringComparison.OrdinalIgnoreCase));
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
        Assert.Equal(Deluno.Quality.MediaPolicyCatalog.CurrentVersion, decision.PolicyVersion);
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

    // ── Acquisition profiles (#316) ─────────────────────────────────────

    [Fact]
    public void Decide_rejects_when_a_release_profile_required_term_is_missing()
    {
        var decision = ReleaseDecisionEngine.Decide(GoodInput(
            releaseProfiles: [Profile(mustContain: "Remux")]));

        Assert.Equal("rejected", decision.Status);
        Assert.Contains(decision.RiskFlags, risk => risk.Contains("missing required term 'Remux'", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_rejects_when_a_release_profile_excluded_term_is_present()
    {
        var decision = ReleaseDecisionEngine.Decide(GoodInput(
            releaseName: "Movie.2024.1080p.WEB-DL.CAM-GRP",
            releaseProfiles: [Profile(mustNotContain: "CAM")]));

        Assert.Equal("rejected", decision.Status);
        Assert.Contains(decision.RiskFlags, risk => risk.Contains("excluded term 'CAM'", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_holds_a_release_until_the_profile_delay_clears()
    {
        var decision = ReleaseDecisionEngine.Decide(GoodInput(
            indexerProtocol: "newznab",
            releaseAgeHours: 0.5,
            releaseProfiles: [Profile(preferredProtocol: "usenet", usenetDelayMinutes: 60)]));

        Assert.Equal("delayed", decision.Status);
        Assert.Contains(decision.RiskFlags, risk => risk.Contains("acquisition delay is active", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_scores_matching_profile_terms_and_protocol()
    {
        var decision = ReleaseDecisionEngine.Decide(GoodInput(
            indexerProtocol: "newznab",
            releaseProfiles: [Profile(
                preferredProtocol: "usenet",
                preferredTerms: [new ReleaseTermScore("WEB-DL", 125)])]));

        Assert.Equal("preferred", decision.Status);
        Assert.Contains(decision.Reasons, reason => reason.Contains("WEB-DL", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(decision.Reasons, reason => reason.Contains("Preferred usenet protocol", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_rejects_when_release_exceeds_indexer_size_limit()
    {
        var decision = ReleaseDecisionEngine.Decide(GoodInput(
            sizeBytes: 2_000_000_000L,
            maximumSizeMb: 1000));

        Assert.Equal("rejected", decision.Status);
        Assert.Contains(decision.RiskFlags, risk => risk.Contains("maximum", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_prefers_matching_indexer_flags()
    {
        var withoutFlag = ReleaseDecisionEngine.Decide(GoodInput());
        var withFlag = ReleaseDecisionEngine.Decide(GoodInput(
            indexerFlags: "freeleech, double-upload",
            preferIndexerFlags: "freeleech"));

        Assert.Equal("preferred", withFlag.Status);
        Assert.True(withFlag.Score > withoutFlag.Score);
        Assert.Contains(withFlag.Reasons, reason => reason.Contains("freeleech", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_uses_the_persisted_current_file_facts_when_the_plan_hash_matches()
    {
        var plan = SnapshotPlan();
        var currentFacts = new[]
        {
            new PreferenceFact("quality.web-1080p", PreferenceFactState.Present),
            new PreferenceFact("audio.format.truehd", PreferenceFactState.Absent),
            new PreferenceFact("audio.format.dts", PreferenceFactState.Present)
        };
        var snapshot = new PreferenceEvaluationSnapshot(
            "movie-1",
            "library-1",
            "file-1",
            "/library/Movie.2024.1080p.WEB-DL.DTS-GRP.mkv",
            100,
            plan.Id,
            plan.Version,
            plan.PlanHash,
            currentFacts,
            ReleasePreferenceEvaluator.Evaluate(plan, currentFacts),
            [],
            DateTimeOffset.UnixEpoch,
            "test");

        var decision = ReleaseDecisionEngine.Decide(new ReleaseDecisionInput(
            ReleaseName: "Movie.2024.1080p.WEB-DL.TrueHD-GRP",
            Quality: "WEB 1080p",
            CurrentQuality: "WEB 1080p",
            TargetQuality: "WEB 1080p",
            SizeBytes: 2_000_000_000,
            Seeders: 10,
            DownloadUrl: "https://example.test/release",
            SourcePriorityScore: 100,
            CustomFormatScore: 0,
            PreferencePlan: plan,
            CurrentPreferenceEvaluation: snapshot));

        Assert.Equal("preferred", decision.Status);
        Assert.Equal(PreferenceCandidateStatus.Upgrade, decision.PreferenceComparison?.Status);
        Assert.Contains(decision.PreferenceComparison?.Reasons ?? [], reason => reason.Contains("Audio", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_does_not_compare_a_snapshot_from_a_different_plan_version()
    {
        var plan = SnapshotPlan();
        var facts = new[]
        {
            new PreferenceFact("quality.web-1080p", PreferenceFactState.Present),
            new PreferenceFact("audio.format.truehd", PreferenceFactState.Absent),
            new PreferenceFact("audio.format.dts", PreferenceFactState.Present)
        };
        var evaluation = ReleasePreferenceEvaluator.Evaluate(plan, facts);
        var stale = new PreferenceEvaluationSnapshot(
            "movie-1",
            "library-1",
            "file-1",
            "/library/Movie.2024.1080p.WEB-DL.DTS-GRP.mkv",
            100,
            plan.Id,
            "old-version",
            "old-plan-hash",
            facts,
            evaluation,
            [],
            DateTimeOffset.UnixEpoch,
            "test");

        var decision = ReleaseDecisionEngine.Decide(new ReleaseDecisionInput(
            ReleaseName: "Movie.2024.1080p.WEB-DL.TrueHD-GRP",
            Quality: "WEB 1080p",
            CurrentQuality: "WEB 1080p",
            TargetQuality: "WEB 1080p",
            SizeBytes: 2_000_000_000,
            Seeders: 10,
            DownloadUrl: "https://example.test/release",
            SourcePriorityScore: 100,
            CustomFormatScore: 0,
            PreferencePlan: plan,
            CurrentPreferenceEvaluation: stale,
            CurrentFilePresent: true));

        Assert.True(decision.Status == "held", $"{decision.Status}: {decision.Summary} | {string.Join("; ", decision.Reasons)} | {string.Join("; ", decision.RiskFlags)}");
        Assert.Equal(PreferenceCandidateStatus.NeedsReview, decision.PreferenceComparison?.Status);
        Assert.Contains(decision.Reasons, reason => reason.Contains("re-evaluate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_holds_an_installed_file_when_no_same_plan_baseline_can_be_rebuilt()
    {
        var decision = ReleaseDecisionEngine.Decide(new ReleaseDecisionInput(
            ReleaseName: "Movie.2024.1080p.WEB-DL.TrueHD-GRP",
            Quality: "WEB 1080p",
            CurrentQuality: null,
            TargetQuality: "WEB 1080p",
            SizeBytes: 2_000_000_000,
            Seeders: 10,
            DownloadUrl: "https://example.test/release",
            SourcePriorityScore: 100,
            CustomFormatScore: 0,
            PreferencePlan: SnapshotPlan(),
            CurrentFilePresent: true));

        Assert.Equal("held", decision.Status);
        Assert.Contains(
            decision.Reasons,
            reason => reason.Contains("installed-file preference evidence is missing", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            decision.RiskFlags,
            risk => risk.Contains("cannot prove", StringComparison.OrdinalIgnoreCase));
    }

    private static ReleasePreferencePlan SnapshotPlan()
        => new(
            Id: "snapshot-test",
            Version: "1",
            MediaType: "movies",
            Families:
            [
                new PreferenceFamily(
                    "quality",
                    "Quality",
                    1,
                    PreferenceIntent.Ranked,
                    [new PreferenceFamilyLevel("web-1080", 0, ["quality.web-1080p"])],
                    TargetLevelId: "web-1080"),
                new PreferenceFamily(
                    "audio",
                    "Audio",
                    2,
                    PreferenceIntent.Ranked,
                    [
                        new PreferenceFamilyLevel("truehd", 0, ["audio.format.truehd"]),
                        new PreferenceFamilyLevel("dts", 1, ["audio.format.dts"])
                    ],
                    TargetLevelId: "truehd")
            ],
            DimensionOrder: ["quality", "audio"]);

    private static ReleaseProfileItem Profile(
        string preferredProtocol = "any",
        int usenetDelayMinutes = 0,
        string mustContain = "",
        string mustNotContain = "",
        IReadOnlyList<ReleaseTermScore>? preferredTerms = null)
        => new(
            Id: Guid.NewGuid().ToString("N"),
            Name: "Test profile",
            TagName: "",
            PreferredProtocol: preferredProtocol,
            UsenetDelayMinutes: usenetDelayMinutes,
            TorrentDelayMinutes: 0,
            MustContain: mustContain,
            MustNotContain: mustNotContain,
            PreferredTerms: preferredTerms ?? [],
            CreatedUtc: DateTimeOffset.UnixEpoch,
            UpdatedUtc: DateTimeOffset.UnixEpoch);
}
