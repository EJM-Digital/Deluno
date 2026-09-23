using Deluno.Integrations.Search;
using Deluno.Quality;
using Deluno.Quality.ReleasePreferences;

namespace Deluno.Integrations.Tests.Search;

/// <summary>
/// #597: a held file below its tier's size floor fails the profile, so a copy
/// within the range replaces it.
///
/// <para><b>Why this file exists.</b> On the soak rig Cleaner was held as a
/// WEB 1080p below the profile's WEB 1080p floor. An 18 GB Bluray 1080p was
/// offered and held as "equivalent": the floor turned down releases that size
/// but never looked at the file itself.</para>
/// </summary>
public sealed class AHeldFileBelowTheSizeFloorIsReplacedTests
{
    private static readonly IReadOnlyList<ProfileSizeRule> Floor = [new ProfileSizeRule("WEB 1080p", 4, 0, 420, 0)];

    [Fact]
    public void A_file_below_the_floor_fails_a_hard_gate_and_says_why()
    {
        var result = AcquisitionDecisionPipeline.WithInstalledSizeFloor(Snapshot(sizeGb: 1.2), "WEB 1080p", Floor, "movies", episodeScope: false);

        Assert.False(result!.Evaluation.HardGatesPassed);
        Assert.Equal(PreferenceEvaluationStatus.BelowGoal, result.Evaluation.Status);
        Assert.Equal("Your file is 1.2 GB, below the 4.0 GB minimum for WEB 1080p.", result.Evaluation.InstalledFileViolation);
        Assert.Contains(result.Evaluation.Reasons, reason => reason == "Your file is 1.2 GB, below the 4.0 GB minimum for WEB 1080p.");
    }

    [Fact]
    public void A_file_within_its_range_or_a_tier_with_no_rule_is_left_as_it_was()
    {
        var within = Snapshot(sizeGb: 6);
        Assert.Same(within, AcquisitionDecisionPipeline.WithInstalledSizeFloor(within, "WEB 1080p", Floor, "movies", episodeScope: false));

        var small = Snapshot(sizeGb: 1.2);
        Assert.Same(small, AcquisitionDecisionPipeline.WithInstalledSizeFloor(small, "Bluray 1080p", Floor, "movies", episodeScope: false));
        Assert.Same(small, AcquisitionDecisionPipeline.WithInstalledSizeFloor(small, "WEB 1080p", [], "movies", episodeScope: false));
        Assert.Null(AcquisitionDecisionPipeline.WithInstalledSizeFloor(null, "WEB 1080p", Floor, "movies", episodeScope: false));
    }

    /// <summary>
    /// #600: an episode's file is measured against the episode band, and a
    /// show-level search has no one file to measure.
    /// </summary>
    [Fact]
    public void An_episode_is_measured_in_the_episode_band_and_a_show_is_not_measured()
    {
        var ordinary = Snapshot(sizeGb: 1.2);
        Assert.Same(ordinary, AcquisitionDecisionPipeline.WithInstalledSizeFloor(ordinary, "WEB 1080p", Floor, "tv", episodeScope: true));
        Assert.Same(ordinary, AcquisitionDecisionPipeline.WithInstalledSizeFloor(ordinary, "WEB 1080p", Floor, "tv", episodeScope: false));

        var tiny = AcquisitionDecisionPipeline.WithInstalledSizeFloor(Snapshot(sizeGb: 0.2), "WEB 1080p", Floor, "tv", episodeScope: true);
        Assert.False(tiny!.Evaluation.HardGatesPassed);
        Assert.Contains(tiny.Evaluation.Reasons, reason => reason == "Your file is 205 MB, below the 420 MB minimum for WEB 1080p.");
    }

    private static PreferenceEvaluationSnapshot Snapshot(double sizeGb)
        => new(
            "movie-cleaner",
            "library-movies",
            "file-1",
            @"\\storage\Movies\Cleaner (2007)\Cleaner.mkv",
            (long)(sizeGb * 1_073_741_824d),
            "plan",
            "1",
            "hash",
            [],
            new PreferenceEvaluation("plan", "1", "hash", PreferenceEvaluationStatus.MeetsPlan, hardGatesPassed: true, targetsMet: true, [], []),
            [],
            DateTimeOffset.Parse("2026-09-23T12:00:00Z"));
}
