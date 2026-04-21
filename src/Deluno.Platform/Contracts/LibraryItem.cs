namespace Deluno.Platform.Contracts;

public sealed record LibraryItem(
    string Id,
    string Name,
    string MediaType,
    string Purpose,
    string RootPath,
    string? DownloadsPath,
    bool AutoSearchEnabled,
    int SearchIntervalHours,
    int RetryDelayHours,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);
