namespace Deluno.Connections.Contracts;

public sealed record CreateIndexerRequest(
    string? Name,
    string? Protocol,
    string? Privacy,
    string? BaseUrl,
    string? ApiKey,
    int? Priority,
    string? Categories,
    string? Tags,
    string? MediaScope,
    bool IsEnabled,
    int? RequestIntervalSeconds = null,
    // Null means "inherit the global sharing rule" (#288).
    string? SharingMode = null,
    int? SharingForHours = null,
    double? SharingUntilRatio = null,
    string? SharingStuckAction = null,
    int? SharingStuckAfterDays = null);
