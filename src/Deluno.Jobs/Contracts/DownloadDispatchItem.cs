namespace Deluno.Jobs.Contracts;

public sealed record DownloadDispatchItem(
    string Id,
    string LibraryId,
    string MediaType,
    string EntityType,
    string EntityId,
    string ReleaseName,
    string IndexerName,
    string DownloadClientId,
    string DownloadClientName,
    string Status,
    string? NotesJson,
    DateTimeOffset CreatedUtc,

    // Grab outcome
    string? GrabStatus,
    DateTimeOffset? GrabAttemptedUtc,
    int? GrabResponseCode,
    string? GrabMessage,
    string? GrabFailureCode,
    string? GrabResponseJson,

    // Detection (from polling)
    DateTimeOffset? DetectedUtc,
    string? TorrentHashOrItemId,
    long? DownloadedBytes,

    // Import outcome
    string? ImportStatus,
    DateTimeOffset? ImportDetectedUtc,
    DateTimeOffset? ImportCompletedUtc,
    string? ImportedFilePath,
    string? ImportFailureCode,
    string? ImportFailureMessage,

    // Circuit breaker
    DateTimeOffset? CircuitOpenUntilUtc,

    // Retry tracking
    DateTimeOffset? NextRetryEligibleUtc = null,
    int? AttemptCount = null);
