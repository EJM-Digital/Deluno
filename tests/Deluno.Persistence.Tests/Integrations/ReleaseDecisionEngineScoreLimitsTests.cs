using Deluno.Integrations.Search;
using Deluno.Quality;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// The three TRaSH-style guardrails behind "Never grab copies rated bad",
/// "Stop upgrading once a copy is..." and "Only replace when..." (#778), and
/// the one-sentence plain reason each produces (#938).
/// </summary>
public sealed class ReleaseDecisionEngineScoreLimitsTests
{
    private static ReleaseDecisionInput Input(
        int customFormatScore,
        int? currentCustomFormatScore,
        ProfileScoreLimits? scoreLimits,
        string currentQuality = "WEB 1080p",
        string targetQuality = "WEB 1080p",
        string quality = "WEB 1080p")
        => new(
            ReleaseName: "Movie.2024.1080p.WEB-DL.DDP5.1-GRP",
            Quality: quality,
            CurrentQuality: currentQuality,
            TargetQuality: targetQuality,
            SizeBytes: 8_000_000_000,
            Seeders: 25,
            DownloadUrl: "https://example.test/file.torrent",
            SourcePriorityScore: 100,
            CustomFormatScore: customFormatScore,
            CurrentCustomFormatScore: currentCustomFormatScore,
            ScoreLimits: scoreLimits);

    // ── Never grab copies TRaSH rates as bad (minFormatScore) ─────────────

    [Fact]
    public void A_release_below_the_minimum_score_is_refused_with_a_plain_reason()
    {
        var decision = ReleaseDecisionEngine.Decide(Input(
            customFormatScore: 50,
            currentCustomFormatScore: null,
            scoreLimits: new ProfileScoreLimits(MinFormatScore: 100)));

        Assert.Equal("rejected", decision.Status);
        Assert.Contains(decision.Reasons.Concat(decision.RiskFlags), text =>
            text.Contains("TRaSH rates this copy as bad", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_release_at_or_above_the_minimum_score_is_not_refused_by_it()
    {
        var decision = ReleaseDecisionEngine.Decide(Input(
            customFormatScore: 100,
            currentCustomFormatScore: null,
            scoreLimits: new ProfileScoreLimits(MinFormatScore: 100)));

        Assert.NotEqual("rejected", decision.Status);
    }

    [Fact]
    public void No_minimum_set_never_refuses_on_score_alone()
    {
        var decision = ReleaseDecisionEngine.Decide(Input(
            customFormatScore: -5000,
            currentCustomFormatScore: null,
            scoreLimits: null));

        Assert.NotEqual("rejected", decision.Status);
    }

    // ── Stop upgrading once a copy is Good/Great/Best (cutoffFormatScore) ──

    [Fact]
    public void Once_the_installed_file_meets_the_cutoff_score_upgrading_stops()
    {
        var decision = ReleaseDecisionEngine.Decide(Input(
            customFormatScore: 6500,
            currentCustomFormatScore: 6000,
            scoreLimits: new ProfileScoreLimits(CutoffFormatScore: 6000)));

        Assert.Equal("rejected", decision.Status);
        Assert.Contains(decision.Reasons.Concat(decision.RiskFlags), text =>
            text.Contains("Stopped upgrading", StringComparison.OrdinalIgnoreCase)
            && text.Contains("already", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Below_the_cutoff_score_upgrading_still_continues()
    {
        var decision = ReleaseDecisionEngine.Decide(Input(
            customFormatScore: 6500,
            currentCustomFormatScore: 5000,
            scoreLimits: new ProfileScoreLimits(CutoffFormatScore: 6000)));

        Assert.NotEqual("rejected", decision.Status);
    }

    [Fact]
    public void No_cutoff_set_never_stops_on_score_alone()
    {
        // A genuine improvement over the installed file's score, so the
        // pre-existing "equivalent replacement" gate (which fires whenever a
        // candidate does not improve on the installed score, with or without
        // a configured cutoff) does not also explain a rejection here.
        var decision = ReleaseDecisionEngine.Decide(Input(
            customFormatScore: 20001,
            currentCustomFormatScore: 20000,
            scoreLimits: null));

        Assert.NotEqual("rejected", decision.Status);
    }

    // ── Only replace when clearly/much better (minUpgradeFormatScore) ─────

    [Fact]
    public void A_candidate_that_gains_less_than_the_minimum_upgrade_is_refused()
    {
        var decision = ReleaseDecisionEngine.Decide(Input(
            customFormatScore: 101,
            currentCustomFormatScore: 100,
            scoreLimits: new ProfileScoreLimits(MinUpgradeFormatScore: 5)));

        Assert.Equal("rejected", decision.Status);
        Assert.Contains(decision.Reasons.Concat(decision.RiskFlags), text =>
            text.Contains("Not grabbed", StringComparison.OrdinalIgnoreCase)
            && text.Contains("what you asked for", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_candidate_that_gains_at_least_the_minimum_upgrade_is_accepted()
    {
        var decision = ReleaseDecisionEngine.Decide(Input(
            customFormatScore: 106,
            currentCustomFormatScore: 100,
            scoreLimits: new ProfileScoreLimits(MinUpgradeFormatScore: 5)));

        Assert.NotEqual("rejected", decision.Status);
    }

    [Fact]
    public void A_candidate_of_a_higher_quality_tier_is_not_held_to_the_upgrade_gain_limit()
    {
        // The upgrade-gain limit only governs same-quality-tier replacements;
        // a genuine quality-tier improvement is judged on quality, not score.
        var decision = ReleaseDecisionEngine.Decide(Input(
            customFormatScore: 0,
            currentCustomFormatScore: 100,
            scoreLimits: new ProfileScoreLimits(MinUpgradeFormatScore: 50),
            currentQuality: "WEB 1080p",
            targetQuality: "WEB 2160p",
            quality: "WEB 2160p"));

        Assert.NotEqual("rejected", decision.Status);
    }
}
