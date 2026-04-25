namespace Deluno.Platform.Contracts;

public sealed record CreateCustomFormatRequest(
    string Name,
    string? MediaType,
    int Score,
    string? Conditions,
    bool UpgradeAllowed);
