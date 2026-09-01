namespace Deluno.Quality.Contracts;

/// <summary>
/// A dependency conflict that prevents a historical Media Plan snapshot from
/// being applied without silently changing its meaning.
/// </summary>
public sealed record MediaPlanRollbackConflict(
    string Code,
    string Message,
    string PlanId,
    int TargetVersion,
    string? QualityProfileId,
    ReleasePreferencePlanReference? RequestedReleasePreferencePlan,
    ReleasePreferencePlanReference? CurrentReleasePreferencePlan);

/// <summary>
/// Checks the immutable dependencies captured by a Media Plan version before
/// the mutable policy row is changed. Rollback is intentionally fail-closed:
/// an owner can review and repair the dependency, but the server must not
/// apply a snapshot with a different release-preference meaning.
/// </summary>
public static class MediaPlanRollbackGuard
{
    public static MediaPlanRollbackConflict? Check(
        string planId,
        int targetVersion,
        MediaPlanSnapshot snapshot,
        QualityProfileItem? qualityProfile)
    {
        if (snapshot.QualityProfileId is null)
        {
            return snapshot.ReleasePreferencePlan is null
                ? null
                : Conflict(
                    "release_preference_without_profile",
                    "This Media Plan version records a release-preference plan but no quality profile. Review the snapshot before restoring it.",
                    planId,
                    targetVersion,
                    snapshot,
                    qualityProfile);
        }

        if (qualityProfile is null)
        {
            return Conflict(
                "quality_profile_missing",
                "The quality profile recorded by this Media Plan version no longer exists. Restore or choose the dependency before rolling back.",
                planId,
                targetVersion,
                snapshot,
                qualityProfile);
        }

        if (!string.Equals(
                NormalizeMediaType(snapshot.MediaType),
                NormalizeMediaType(qualityProfile.MediaType),
                StringComparison.Ordinal))
        {
            return Conflict(
                "quality_profile_media_type_changed",
                "The quality profile recorded by this Media Plan version now has a different media type. Review the dependency before rolling back.",
                planId,
                targetVersion,
                snapshot,
                qualityProfile);
        }

        if (!ReferencesEqual(snapshot.ReleasePreferencePlan, qualityProfile.ReleasePreferencePlan))
        {
            return Conflict(
                "release_preference_reference_changed",
                "The release-preference plan referenced by this Media Plan version has changed on its quality profile. Review the new reference before rolling back.",
                planId,
                targetVersion,
                snapshot,
                qualityProfile);
        }

        return null;
    }

    private static MediaPlanRollbackConflict Conflict(
        string code,
        string message,
        string planId,
        int targetVersion,
        MediaPlanSnapshot snapshot,
        QualityProfileItem? qualityProfile)
        => new(
            code,
            message,
            planId,
            targetVersion,
            snapshot.QualityProfileId,
            ReleasePreferencePlanReference.Normalize(snapshot.ReleasePreferencePlan),
            ReleasePreferencePlanReference.Normalize(qualityProfile?.ReleasePreferencePlan));

    private static bool ReferencesEqual(
        ReleasePreferencePlanReference? left,
        ReleasePreferencePlanReference? right)
    {
        left = ReleasePreferencePlanReference.Normalize(left);
        right = ReleasePreferencePlanReference.Normalize(right);
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return string.Equals(left.PlanId, right.PlanId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Version, right.Version, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.PlanHash, right.PlanHash, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeMediaType(string? value)
        => string.Equals(value?.Trim(), "tv", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value?.Trim(), "tv shows", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value?.Trim(), "tvshows", StringComparison.OrdinalIgnoreCase)
            ? "tv"
            : "movies";
}
