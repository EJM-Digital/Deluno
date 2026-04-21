namespace Deluno.Platform.Contracts;

public sealed record CreateQualityProfileRequest(
    string? Name,
    string? MediaType,
    string? CutoffQuality,
    string? AllowedQualities,
    bool UpgradeUntilCutoff,
    bool UpgradeUnknownItems);
