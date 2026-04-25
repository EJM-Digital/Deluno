namespace Deluno.Platform.Contracts;

public sealed record CustomFormatItem(
    string Id,
    string Name,
    string MediaType,
    int Score,
    string Conditions,
    bool UpgradeAllowed,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);
