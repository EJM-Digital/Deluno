namespace Deluno.Series.Contracts;

/// <summary>
/// One series found on disk during an existing-library import, with the
/// episodes detected alongside it.
///
/// Grouping the arguments into a record is what makes batching possible: the
/// import used to be one call and one transaction per show, and a show carries
/// its whole episode list with it — a few thousand shows at 50-100 episodes
/// each is a couple of hundred thousand rows.
/// </summary>
public sealed record ExistingSeriesImportRequest(
    string Title,
    int? StartYear,
    string WantedStatus,
    string WantedReason,
    string? CurrentQuality,
    string? TargetQuality,
    bool QualityCutoffMet,
    bool UnmonitorWhenCutoffMet,
    string? FilePath,
    long? FileSizeBytes,
    IReadOnlyList<ImportedEpisodeItem>? Episodes);
