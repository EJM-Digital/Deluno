using Deluno.Libraries.Contracts;

namespace Deluno.Platform.Contracts;

/// <summary>
/// What a processor says it did with a file it could not process (#497).
/// </summary>
public static class ProcessorDispositions
{
    /// <summary>The processor is keeping the original. Nothing was deleted.</summary>
    public const string Held = "held";

    /// <summary>The processor deleted the download because it could not be processed.</summary>
    public const string Rejected = "rejected";

    public static string? Normalize(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            Held => Held,
            Rejected => Rejected,
            _ => null
        };
}

/// <summary>
/// The capability a processor looks for before it offers to delete a file it
/// cannot process. Advertised only because Deluno acts on the report: the file
/// is refused, the client forgets it, and another copy is searched for.
/// </summary>
public static class ProcessorCapabilities
{
    public const string RejectRegrab = "processor-reject-regrab";
}

public sealed record ProcessorFailureReport(
    LibraryItem Library,
    ProcessorHandoffItem Handoff,
    string ProcessorName,
    string Message,
    string? FailureClass);

/// <param name="Acted">
/// Whether the release is now refused. A processor deletes its copy only on a
/// success answer, so anything short of a committed refusal has to say no.
/// </param>
public sealed record ProcessorRejectionOutcome(
    bool Acted,
    string Message,
    string? BlockedReleaseId = null,
    string? SearchJobId = null);

/// <summary>
/// Acting on a processor's final failure report. Lives behind an interface
/// because the answer touches the blocklist, the download client, the search
/// schedule and the catalogues, none of which this module owns.
/// </summary>
public interface IProcessorFailureReportHandler
{
    /// <summary>
    /// The processor deleted the download: refuse that release, have the
    /// client forget it, and search for another copy.
    /// </summary>
    Task<ProcessorRejectionOutcome> HandleRejectedAsync(
        ProcessorFailureReport report,
        CancellationToken cancellationToken);

    /// <summary>
    /// The processor is keeping the original: raise it in Queue recovery so a
    /// person sees it, rather than leaving it in activity alone.
    /// </summary>
    Task HandleHeldAsync(
        ProcessorFailureReport report,
        CancellationToken cancellationToken);
}
