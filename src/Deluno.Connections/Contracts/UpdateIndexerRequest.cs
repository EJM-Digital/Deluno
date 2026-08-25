namespace Deluno.Connections.Contracts;

/// <summary>
/// Patch-style request — null values mean "leave unchanged".
/// Sent by PUT /api/indexers/{id}
/// </summary>
public sealed record UpdateIndexerRequest(
    string? Name,
    string? Protocol,
    string? Privacy,
    string? BaseUrl,
    string? ApiKey,
    int? Priority,
    string? Categories,
    string? Tags,
    string? MediaScope,
    bool? IsEnabled,
    int? RequestIntervalSeconds = null,
    bool? ClearRequestInterval = null,
    // Null means "leave as is". ClearSharingPolicy drops the override entirely
    // so the source goes back to inheriting the global rule (#288).
    string? SharingMode = null,
    int? SharingForHours = null,
    double? SharingUntilRatio = null,
    string? SharingStuckAction = null,
    int? SharingStuckAfterDays = null,
    bool? ClearSharingPolicy = null);
