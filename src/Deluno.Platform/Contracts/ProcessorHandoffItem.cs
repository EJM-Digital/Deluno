namespace Deluno.Platform.Contracts;

/// <summary>
/// Durable ownership and lifecycle record for one completed download that must be
/// processed before Deluno imports it. It is intentionally separate from Activity:
/// Activity explains events, while this record correlates the external callback,
/// clean output, and eventual import job.
/// </summary>
public sealed record ProcessorHandoffItem(
    string Id,
    string LibraryId,
    string MediaType,
    string ClientId,
    string QueueItemId,
    string ReleaseName,
    string SourcePath,
    string? ProcessorName,
    string Status,
    string? OutputPath,
    string? ImportJobId,
    string? FailureMessage,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    ProcessorHandoffObservation? Processor = null,
    ProcessorOutcomeRecord? Outcome = null);

/// <param name="DownloadAddedUtc">
/// When the download client took this download, when it says. A torrent's queue
/// id is its content hash, so fetching the same release again reuses the id;
/// this is what tells the new download from the old one.
/// </param>
public sealed record CreateProcessorHandoffRequest(
    string LibraryId,
    string MediaType,
    string ClientId,
    string QueueItemId,
    string ReleaseName,
    string SourcePath,
    string? ProcessorName,
    DateTimeOffset? DownloadAddedUtc = null);
