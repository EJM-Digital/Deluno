using Deluno.Quality.Data;
using Deluno.Quality.Contracts;
using Deluno.Quality.ReleasePreferences;

namespace Deluno.Quality;

/// <summary>
/// Reads the tiers a quality profile permits.
///
/// This exists because the allowed list used to be stored, validated and shown
/// in the UI without ever reaching the release decision: the search planner only
/// received the cutoff, so a profile permitting up to Bluray 1080p would grab
/// WEB 2160p and score it as the preferred candidate. Every caller that builds
/// an acquisition request resolves the list through here so the same profile
/// means the same thing on every search path.
/// </summary>
public static class QualityProfileResolver
{
    /// <summary>
    /// Resolves the immutable plan attached to a profile. A referenced plan
    /// must be present and hash-valid; silently recompiling a migrated profile
    /// from a newer guide would change its meaning without a plan version.
    /// Profiles without a reference use the normal runtime compiler.
    /// </summary>
    public static async Task<ReleasePreferencePlan?> ResolveReleasePreferencePlanAsync(
        IQualityRepository repository,
        IReleasePreferencePlanRepository? planRepository,
        string? qualityProfileId,
        CancellationToken cancellationToken,
        IReadOnlyList<CustomFormatItem>? customFormats = null,
        // #767: the global Proper/Repack acquisition choice. Callers that do
        // not pass one keep the compiler's own default ("prefer-and-upgrade"),
        // unchanged from before this setting existed.
        string? properRepackAcquisitionMode = null)
    {
        if (string.IsNullOrWhiteSpace(qualityProfileId))
        {
            return null;
        }

        var profile = (await repository.ListQualityProfilesAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Id, qualityProfileId, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            return null;
        }

        if (profile.ReleasePreferencePlan is not { } reference)
        {
            // Do not return null and let each downstream consumer invent its
            // own fallback identity. The probe/import path and acquisition
            // path must compile the same profile-scoped plan or a valid
            // installed snapshot is rejected as stale forever.
            customFormats ??= await repository.ListCustomFormatsAsync(cancellationToken);
            return ReleasePreferencePlanFactory.CreateQualityPlan(profile, customFormats, properRepackAcquisitionMode: properRepackAcquisitionMode);
        }

        if (planRepository is null)
        {
            throw new InvalidOperationException(
                $"Quality profile '{profile.Name}' references immutable release-preference plan '{reference.PlanId}' version '{reference.Version}', but the plan store is unavailable.");
        }

        var stored = await planRepository.GetAsync(reference.PlanId, reference.Version, cancellationToken);
        if (stored is null)
        {
            throw new InvalidDataException(
                $"Quality profile '{profile.Name}' references release-preference plan '{reference.PlanId}' version '{reference.Version}', but that immutable plan is missing.");
        }

        if (!string.Equals(reference.PlanHash, stored.PlanHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(reference.PlanHash, stored.Plan.PlanHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Quality profile '{profile.Name}' has a release-preference plan reference whose hash does not match the immutable stored plan.");
        }

        return stored.Plan;
    }

    /// <summary>
    /// Everything a profile says about a search, read from the profile once
    /// (#599).
    ///
    /// <para>Every search path used to ask for these one at a time, and the
    /// copies drifted: a person's Search now judged by the profile's size
    /// rules, upgrade stop and acquisition rules, while every automatic search
    /// left all three out. One answer, taken whole, cannot be taken in part.</para>
    /// </summary>
    public static async Task<ProfileSearchRules> ResolveSearchRulesAsync(
        IQualityRepository repository,
        string? qualityProfileId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(qualityProfileId))
        {
            return ProfileSearchRules.None;
        }

        var profile = (await repository.ListQualityProfilesAsync(cancellationToken))
            .FirstOrDefault(item => item.Id == qualityProfileId);
        if (profile is null)
        {
            return ProfileSearchRules.None;
        }

        var acquisition = profile.Acquisition?.Normalize();
        var scoreLimits = profile.ScoreLimits?.Normalize();
        return new ProfileSearchRules(
            ParseAllowedQualities(profile.AllowedQualities),
            profile.UpgradeUntilCutoff,
            profile.SizeRules ?? [],
            acquisition is null || acquisition.IsEmpty ? null : acquisition,
            profile.UpgradeStop,
            scoreLimits is null || scoreLimits.IsEmpty ? null : scoreLimits);
    }

    /// <summary>Splits the stored comma-separated tier list.</summary>
    public static IReadOnlyList<string> ParseAllowedQualities(string? allowedQualities)
        => string.IsNullOrWhiteSpace(allowedQualities)
            ? []
            : allowedQualities.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>What one profile says about a search (#599). See <see cref="QualityProfileResolver.ResolveSearchRulesAsync"/>.</summary>
public sealed record ProfileSearchRules(
    IReadOnlyList<string> AllowedQualities,
    bool UpgradeUntilCutoff,
    IReadOnlyList<ProfileSizeRule> SizeRules,
    ProfileAcquisitionRules? Acquisition,
    QualityUpgradeStopPolicy? UpgradeStop,
    /// <summary>The minimum, cutoff and minimum-upgrade-gain score guardrails this profile set (#778).</summary>
    ProfileScoreLimits? ScoreLimits = null)
{
    /// <summary>No profile: every tier allowed, keep upgrading to the cutoff, no size, acquisition, stop or score opinion.</summary>
    public static readonly ProfileSearchRules None = new([], true, [], null, null, null);
}
