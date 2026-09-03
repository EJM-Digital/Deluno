namespace Deluno.Quality.Contracts;

public sealed record UpdateQualityProfileRequest(
    string? Name,
    string? CutoffQuality,
    string? AllowedQualities,
    string? CustomFormatIds,
    bool UpgradeUntilCutoff,
    bool UpgradeUnknownItems,
    ReleasePreferencePlanReference? ReleasePreferencePlan = null,
    /// <summary>
    /// This profile's own size answers. Null leaves the stored ones alone; an
    /// empty list clears them, which is how somebody says "no size opinion".
    /// </summary>
    IReadOnlyList<ProfileSizeRule>? SizeRules = null);
