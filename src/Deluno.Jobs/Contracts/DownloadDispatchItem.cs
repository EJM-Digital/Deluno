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
    DateTimeOffset CreatedUtc);
