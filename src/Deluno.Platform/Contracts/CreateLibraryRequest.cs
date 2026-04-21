namespace Deluno.Platform.Contracts;

public sealed record CreateLibraryRequest(
    string? Name,
    string? MediaType,
    string? Purpose,
    string? RootPath,
    string? DownloadsPath,
    bool AutoSearchEnabled,
    int? SearchIntervalHours,
    int? RetryDelayHours);
