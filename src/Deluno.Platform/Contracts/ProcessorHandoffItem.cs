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
    ProcessorOutcomeRecord? Outcome = null,
    /// <summary>The processor connection's id (#1077). <see cref="ProcessorName"/> is its current name for display; every lookup goes by this id.</summary>
    string? ProcessorConnectionId = null,
    /// <summary>
    /// The files of <see cref="SourcePath"/> the processor is asked to take, set only on the copy that goes over the wire and
    /// only when the processor advertises a minimum file size (#1199). Null means the whole folder.
    /// </summary>
    IReadOnlyList<string>? SourceFiles = null);

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
    DateTimeOffset? DownloadAddedUtc = null,
    string? ProcessorConnectionId = null);

/// <summary>
/// An older hand-off of a source path whose id was replaced when the release was sent again (#1208). The processor
/// keeps the id it was given and reports against it, so Deluno keeps it to answer that report.
/// </summary>
public sealed record SupersededProcessorHandoff(
    string Id,
    string LibraryId,
    string ReleaseName,
    string LastStatus,
    string SupersededById,
    DateTimeOffset SupersededUtc,
    DateTimeOffset? SettledUtc);
