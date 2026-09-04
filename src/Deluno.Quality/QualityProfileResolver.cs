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
        IReadOnlyList<CustomFormatItem>? customFormats = null)
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
            return ReleasePreferencePlanFactory.CreateQualityPlan(profile, customFormats);
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
    /// The permitted tiers for a profile, or an empty list when the profile is
    /// absent or does not constrain tiers. Empty means "the cutoff decides",
    /// never "nothing is allowed".
    /// </summary>
    public static async Task<IReadOnlyList<string>> ResolveAllowedQualitiesAsync(
        IQualityRepository repository,
        string? qualityProfileId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(qualityProfileId))
        {
            return [];
        }

        var profiles = await repository.ListQualityProfilesAsync(cancellationToken);
        var profile = profiles.FirstOrDefault(item => item.Id == qualityProfileId);
        return ParseAllowedQualities(profile?.AllowedQualities);
    }

    /// <summary>
    /// How big a file of each tier should be, according to this profile.
    ///
    /// <para>Resolved the same way and at the same moment as the allowed tiers,
    /// because they are two answers to the same question about the same profile
    /// and reading them apart is how they come to disagree.</para>
    /// </summary>
    public static async Task<IReadOnlyList<ProfileSizeRule>> ResolveSizeRulesAsync(
        IQualityRepository repository,
        string? qualityProfileId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(qualityProfileId))
        {
            return [];
        }

        var profiles = await repository.ListQualityProfilesAsync(cancellationToken);
        return profiles.FirstOrDefault(item => item.Id == qualityProfileId)?.SizeRules ?? [];
    }

    /// <summary>
    /// This profile's own acquisition answers, or null when it has none.
    ///
    /// <para>Returned as the profile's own type. Shaping it into the rule the
    /// search combines happens in <c>Deluno.Integrations</c>, which is the
    /// layer that can see both — putting it here would mean this project
    /// referencing the platform contracts to describe one of its own fields.</para>
    /// </summary>
    public static async Task<ProfileAcquisitionRules?> ResolveAcquisitionAsync(
        IQualityRepository repository,
        string? qualityProfileId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(qualityProfileId))
        {
            return null;
        }

        var profiles = await repository.ListQualityProfilesAsync(cancellationToken);
        var acquisition = profiles.FirstOrDefault(item => item.Id == qualityProfileId)?.Acquisition?.Normalize();
        return acquisition is null || acquisition.IsEmpty ? null : acquisition;
    }

    /// <summary>
    /// When this profile stops looking for something better.
    ///
    /// <para>Resolved beside the allowed tiers and the size answers, from the
    /// same profile at the same moment, for the same reason.</para>
    /// </summary>
    public static async Task<QualityUpgradeStopPolicy?> ResolveUpgradeStopAsync(
        IQualityRepository repository,
        string? qualityProfileId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(qualityProfileId))
        {
            return null;
        }

        var profiles = await repository.ListQualityProfilesAsync(cancellationToken);
        return profiles.FirstOrDefault(item => item.Id == qualityProfileId)?.UpgradeStop;
    }

    /// <summary>
    /// Reads the profile's stop-when-target behaviour for acquisition. A missing
    /// profile uses the safe historical default: keep upgrading until the
    /// requested cutoff is met.
    /// </summary>
    public static async Task<bool> ResolveUpgradeUntilCutoffAsync(
        IQualityRepository repository,
        string? qualityProfileId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(qualityProfileId))
        {
            return true;
        }

        var profiles = await repository.ListQualityProfilesAsync(cancellationToken);
        return profiles.FirstOrDefault(item => item.Id == qualityProfileId)?.UpgradeUntilCutoff ?? true;
    }

    /// <summary>Splits the stored comma-separated tier list.</summary>
    public static IReadOnlyList<string> ParseAllowedQualities(string? allowedQualities)
        => string.IsNullOrWhiteSpace(allowedQualities)
            ? []
            : allowedQualities.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
