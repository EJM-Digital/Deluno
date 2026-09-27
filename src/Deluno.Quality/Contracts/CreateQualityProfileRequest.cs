namespace Deluno.Quality.Contracts;

public sealed record CreateQualityProfileRequest(
    string? Name,
    string? MediaType,
    string? CutoffQuality,
    string? AllowedQualities,
    string? CustomFormatIds,
    bool UpgradeUntilCutoff,
    bool UpgradeUnknownItems,
    ReleasePreferencePlanReference? ReleasePreferencePlan = null,
    /// <summary>
    /// This profile's own size answers. Omitted means the typical band for
    /// each allowed tier, written into the profile rather than inherited.
    /// </summary>
    IReadOnlyList<ProfileSizeRule>? SizeRules = null,
    /// <summary>When this profile stops looking for something better.</summary>
    QualityUpgradeStopPolicy? UpgradeStop = null,
    /// <summary>How much this profile cares about each preference it selected.</summary>
    IReadOnlyDictionary<string, string>? FormatIntents = null,
    /// <summary>How this profile wants a release fetched.</summary>
    ProfileAcquisitionRules? Acquisition = null,
    /// <summary>The TRaSH-style score guardrails behind the plain cutoff, upgrade-gain and never-grab-bad choices.</summary>
    ProfileScoreLimits? ScoreLimits = null,
    /// <summary>"Preferred language" for this profile.</summary>
    ProfileLanguagePreference? LanguagePreference = null);
