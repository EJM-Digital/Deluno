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
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);
