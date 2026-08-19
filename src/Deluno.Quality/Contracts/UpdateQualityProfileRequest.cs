namespace Deluno.Quality.Contracts;

public sealed record UpdateQualityProfileRequest(
    string? Name,
    string? CutoffQuality,
    string? AllowedQualities,
    string? CustomFormatIds,
    bool UpgradeUntilCutoff,
    bool UpgradeUnknownItems);
