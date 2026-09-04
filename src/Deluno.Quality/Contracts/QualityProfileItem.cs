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
    IReadOnlyList<ProfileSizeRule>? SizeRules = null,
    /// <summary>
    /// When this profile stops looking for something better. Null means the
    /// behaviour every profile had before it could answer for itself: stop once
    /// the cutoff is met, and require a preference gain to replace a file of
    /// the same quality.
    /// </summary>
    QualityUpgradeStopPolicy? UpgradeStop = null,
    /// <summary>
    /// How much this profile cares about each preference it selected, keyed by
    /// custom-format id. A preference with no answer here keeps the guide's own
    /// recommendation. See <c>ProfileFormatIntents</c>.
    /// </summary>
    IReadOnlyDictionary<string, string>? FormatIntents = null,
    /// <summary>
    /// How this profile wants a release fetched, and the words it insists on or
    /// refuses. Null means it has no acquisition opinion; tag-keyed rules still
    /// apply on top. See <c>ProfileAcquisitionRules</c>.
    /// </summary>
    ProfileAcquisitionRules? Acquisition = null);
