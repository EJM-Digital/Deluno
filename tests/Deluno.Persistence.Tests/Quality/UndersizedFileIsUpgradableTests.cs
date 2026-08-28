using Deluno.Contracts;
using Deluno.Quality;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>
/// A file too small for its own label is a bad copy, and Deluno already knows
/// how to replace bad copies.
///
/// <para><b>What was wrong.</b> "Quality met" was decided from the tier's
/// <i>name</i> alone. On the rig that meant <i>Big Buck Bunny</i>, tagged
/// <c>WEB 2160p</c> and weighing <b>0.06 GB</b> against a rule saying a 2160p
/// film should be 7–60 GB, sat marked <b>Quality met</b> and Deluno was
/// content. The release decision engine does check size — but only on things
/// Deluno downloads, and this arrived through a library import, which never
/// goes near it.</para>
///
/// <para>James: <i>"if there are files that are already under the rules due to
/// a library import or something then its up to deluno to use the upgrade
/// process as the standard process."</i> So it is Upgradable, and the machinery
/// that already exists does the rest. No report, no filter, no person.</para>
/// </summary>
public sealed class UndersizedFileIsUpgradableTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private static readonly IVersionedMediaPolicyEngine Engine = new VersionedMediaPolicyEngine();

    /// <summary>A film at its target tier, which would otherwise be finished.</summary>
    private static MediaWantedDecisionInput AtTarget(long? sizeBytes, long? floorBytes) => new(
        MediaType: "movies",
        HasFile: true,
        CurrentQuality: "WEB 2160p",
        CutoffQuality: "WEB 2160p",
        UpgradeUntilCutoff: true,
        UpgradeUnknownItems: false,
        IsReleased: true,
        FileSizeBytes: sizeBytes,
        SizeFloorBytes: floorBytes);

    [Fact]
    public void A_file_below_its_tiers_floor_is_upgradable_rather_than_finished()
    {
        // Big Buck Bunny's real numbers from the rig.
        var decision = Engine.DecideWantedState(AtTarget((long)(0.06 * Gb), 7 * Gb));

        Assert.Equal(WantedStatuses.Upgrade, decision.WantedStatus);
        Assert.False(decision.QualityCutoffMet);

        // The reason has to name both numbers. "Upgradable" with no explanation
        // on a file that is already at the target tier reads as a bug.
        Assert.Contains("0.06 GB", decision.WantedReason);
        Assert.Contains("7 GB", decision.WantedReason);
    }

    [Fact]
    public void A_file_inside_its_rule_is_still_finished()
    {
        var decision = Engine.DecideWantedState(AtTarget(20 * Gb, 7 * Gb));

        Assert.Equal(WantedStatuses.Covered, decision.WantedStatus);
        Assert.True(decision.QualityCutoffMet);
    }

    /// <summary>
    /// Over the ceiling is a different problem and must not be dressed as this
    /// one.
    /// </summary>
    [Fact]
    public void An_oversized_file_is_not_made_upgradable()
    {
        // 80 GB against a 7–60 GB rule. It is wasted disk, not a bad copy, and
        // searching for something *better* is the opposite of the fix — so this
        // stays finished and the shelf's size filter is where you find it.
        var decision = Engine.DecideWantedState(AtTarget(80 * Gb, 7 * Gb));

        Assert.Equal(WantedStatuses.Covered, decision.WantedStatus);
        Assert.True(decision.QualityCutoffMet);
    }

    /// <summary>
    /// Unknown is never treated as a breach.
    /// </summary>
    [Theory]
    // No size: an import that could not stat the file.
    [InlineData(null, 7L * 1024 * 1024 * 1024)]
    // No rule: a tier the user has left unbounded.
    [InlineData(4L * 1024 * 1024 * 1024, null)]
    // Neither: every caller that predates this change.
    [InlineData(null, null)]
    public void What_is_not_known_is_decided_exactly_as_before(long? sizeBytes, long? floorBytes)
    {
        // Marking these Upgradable would turn a whole library Upgradable the
        // first time somebody imported without sizes, which is worse than the
        // problem being fixed.
        var decision = Engine.DecideWantedState(AtTarget(sizeBytes, floorBytes));

        Assert.Equal(WantedStatuses.Covered, decision.WantedStatus);
        Assert.True(decision.QualityCutoffMet);
    }

    /// <summary>
    /// A title with no file at all is Missing, not Upgradable — the size rule
    /// must not reach past the branch that decides there is nothing to judge.
    /// </summary>
    [Fact]
    public void A_title_with_no_file_is_untouched_by_the_size_rule()
    {
        var decision = Engine.DecideWantedState(new MediaWantedDecisionInput(
            MediaType: "movies",
            HasFile: false,
            CurrentQuality: null,
            CutoffQuality: "WEB 2160p",
            UpgradeUntilCutoff: true,
            UpgradeUnknownItems: false,
            IsReleased: true,
            FileSizeBytes: 0,
            SizeFloorBytes: 7 * Gb));

        Assert.Equal(WantedStatuses.Missing, decision.WantedStatus);
    }
}
