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
    IReadOnlyList<ProfileSizeRule>? SizeRules = null,
    /// <summary>When this profile stops looking for something better.</summary>
    QualityUpgradeStopPolicy? UpgradeStop = null,
    /// <summary>How much this profile cares about each preference it selected.</summary>
    IReadOnlyDictionary<string, string>? FormatIntents = null,
    /// <summary>How this profile wants a release fetched.</summary>
    ProfileAcquisitionRules? Acquisition = null);
