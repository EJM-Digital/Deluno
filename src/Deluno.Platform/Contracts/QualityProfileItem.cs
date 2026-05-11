namespace Deluno.Platform.Contracts;

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
    DateTimeOffset UpdatedUtc);
