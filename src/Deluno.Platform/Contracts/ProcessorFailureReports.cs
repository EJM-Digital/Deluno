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
/// The failure classes a processor names in a failed report that Deluno acts on by name.
/// </summary>
public static class ProcessorFailureClasses
{
    /// <summary>
    /// The download disappeared before the processor could work on it, and stayed gone after its grace period
    /// (#1186). Not a bad release and not a processor fault: there is nothing to try again.
    /// </summary>
    public const string SourceGone = "source_gone";

    public static bool IsSourceGone(string? failureClass)
        => string.Equals(failureClass?.Trim(), SourceGone, StringComparison.OrdinalIgnoreCase);

    /// <summary>The sentence kept on a hand-off whose download was gone. It is the one record of why it ended, also after Dismiss.</summary>
    public static string SourceGoneMessage(string processorName)
        => $"The download was gone before {processorName} could clean it.";

    /// <summary>True when a hand-off's kept sentence is <see cref="SourceGoneMessage"/>, whatever the processor was called.</summary>
    public static bool IsSourceGoneMessage(string? failureMessage)
        => failureMessage is not null
           && failureMessage.StartsWith("The download was gone before ", StringComparison.Ordinal)
           && failureMessage.Contains(" could clean it", StringComparison.Ordinal);
}

/// <summary>What Deluno decided about a report that the download was gone (#1186).</summary>
/// <param name="Settled">True when nothing is left to ask a person: Deluno removed the download, or the title has its file.</param>
/// <param name="Reason">Why it settled, in plain words, for the Activity line. Null when it did not.</param>
public sealed record ProcessorSourceGoneOutcome(bool Settled, string? Reason);

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
    string? FailureClass,
    // Whether the processor deleted the download itself (#1153). Absent or false
    // means it left the download where the client put it and Deluno removes it.
    bool? SourceRemoved = null);

/// <summary>
/// A processor said it made these files, and some of them are not there
/// (#930). The good files still import; this is only about naming what did
/// not, somewhere a person can act on it.
/// </summary>
public sealed record ProcessorMissingOutputReport(
    LibraryItem Library,
    string? EntityId,
    string ProcessorName,
    string ReleaseName,
    string OutputPath,
    IReadOnlyList<string> MissingFiles,
    int TotalOutputFiles);

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

    /// <summary>
    /// The processor says the download was gone before it could work on it (#1186): settled when Deluno removed that
    /// download itself or the title already has its file, otherwise left for a person to answer.
    /// </summary>
    Task<ProcessorSourceGoneOutcome> HandleSourceGoneAsync(
        ProcessorFailureReport report,
        CancellationToken cancellationToken);

    /// <summary>
    /// A processor named files it produced that are not on disk (#930): shown
    /// where a person can act on it -- the title/season page and Health -- not
    /// only in Activity.
    /// </summary>
    Task HandleOutputsMissingAsync(
        ProcessorMissingOutputReport report,
        CancellationToken cancellationToken);
}
