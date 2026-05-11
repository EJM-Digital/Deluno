namespace Deluno.Platform.Contracts;

public sealed record QualityProfilePresetItem(
    string Id,
    string Name,
    string Description,
    string MediaType,
    string CutoffQuality,
    string AllowedQualities,
    bool UpgradeUntilCutoff,
    bool UpgradeUnknownItems,
    int Version);
