namespace Deluno.Platform.Contracts;

public sealed record UpdateCustomFormatRequest(
    string Name,
    string? MediaType,
    int Score,
    string? Conditions,
    bool UpgradeAllowed);
