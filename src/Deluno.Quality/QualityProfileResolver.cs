using Deluno.Quality.Data;

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

    /// <summary>Splits the stored comma-separated tier list.</summary>
    public static IReadOnlyList<string> ParseAllowedQualities(string? allowedQualities)
        => string.IsNullOrWhiteSpace(allowedQualities)
            ? []
            : allowedQualities.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
