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
    DateTimeOffset UpdatedUtc);

public sealed record CreateProcessorHandoffRequest(
    string LibraryId,
    string MediaType,
    string ClientId,
    string QueueItemId,
    string ReleaseName,
    string SourcePath,
    string? ProcessorName);
