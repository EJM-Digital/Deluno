namespace Deluno.Quality.Contracts;

public sealed record QualityProfileItem(
    string Id,
    string Name,
    string MediaType,
    string CutoffQuality,
    string AllowedQualities,
    string CustomFormatIds,
    bool UpgradeUntilCutoff,
    bool UpgradeUnknownItems,
    bool AllowLowerQualityReplacements,
    string? PresetId,
    int? PresetVersion,
    bool PresetDrifted,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    ReleasePreferencePlanReference? ReleasePreferencePlan = null,
    /// <summary>
    /// How big a file of each allowed tier should be, for this profile alone.
    /// Empty means this profile has no size opinion - not that everything is
    /// refused. See <c>ProfileSizeRule</c>.
    /// </summary>
    IReadOnlyList<ProfileSizeRule>? SizeRules = null);
