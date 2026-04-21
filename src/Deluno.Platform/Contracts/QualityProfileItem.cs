namespace Deluno.Platform.Contracts;

public sealed record QualityProfileItem(
    string Id,
    string Name,
    string MediaType,
    string CutoffQuality,
    string AllowedQualities,
    bool UpgradeUntilCutoff,
    bool UpgradeUnknownItems,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);
