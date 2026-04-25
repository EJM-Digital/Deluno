namespace Deluno.Platform.Contracts;

public sealed record CreateQualityProfileRequest(
    string? Name,
    string? MediaType,
    string? CutoffQuality,
    string? AllowedQualities,
    string? CustomFormatIds,
    bool UpgradeUntilCutoff,
    bool UpgradeUnknownItems);
