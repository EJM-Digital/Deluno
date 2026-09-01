using Deluno.Quality.Contracts;

namespace Deluno.Persistence.Tests.Quality;

public sealed class MediaPlanRollbackTests
{
    [Fact]
    public void Matching_quality_profile_reference_allows_rollback()
    {
        var reference = new ReleasePreferencePlanReference("plan-1", "v2", "ABC123");
        var snapshot = Snapshot(reference);
        var profile = Profile(reference);

        Assert.Null(MediaPlanRollbackGuard.Check("media-plan-1", 4, snapshot, profile));
    }

    [Fact]
    public void Changed_release_preference_reference_returns_a_reviewable_conflict()
    {
        var snapshotReference = new ReleasePreferencePlanReference("plan-1", "v2", "ABC123");
        var currentReference = new ReleasePreferencePlanReference("plan-1", "v3", "DEF456");

        var conflict = MediaPlanRollbackGuard.Check(
            "media-plan-1",
            4,
            Snapshot(snapshotReference),
            Profile(currentReference));

        Assert.NotNull(conflict);
        Assert.Equal("release_preference_reference_changed", conflict.Code);
        Assert.Equal(snapshotReference with { PlanHash = "abc123" }, conflict.RequestedReleasePreferencePlan);
        Assert.Equal(currentReference with { PlanHash = "def456" }, conflict.CurrentReleasePreferencePlan);
    }

    [Fact]
    public void Missing_quality_profile_does_not_silently_recreate_a_dependency()
    {
        var conflict = MediaPlanRollbackGuard.Check(
            "media-plan-1",
            2,
            Snapshot(new ReleasePreferencePlanReference("plan-1", "v1", "abc123")),
            null);

        Assert.NotNull(conflict);
        Assert.Equal("quality_profile_missing", conflict.Code);
    }

    [Fact]
    public void Null_profile_and_null_reference_remain_a_valid_snapshot()
    {
        Assert.Null(MediaPlanRollbackGuard.Check("media-plan-1", 1, Snapshot(null, qualityProfileId: null), null));
    }

    private static MediaPlanSnapshot Snapshot(
        ReleasePreferencePlanReference? reference,
        string? qualityProfileId = "quality-1")
        => new(
            "Plan",
            "movies",
            qualityProfileId,
            null,
            string.Empty,
            null,
            null,
            true,
            true,
            null,
            ReleasePreferencePlan: reference);

    private static QualityProfileItem Profile(ReleasePreferencePlanReference? reference)
        => new(
            "quality-1",
            "Quality",
            "movies",
            "WEB 1080p",
            "WEB 1080p",
            string.Empty,
            true,
            false,
            false,
            null,
            null,
            false,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            reference);
}
