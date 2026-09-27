using Deluno.Quality.ReleasePreferences;

namespace Deluno.Movies.Contracts;

/// <summary>
/// One movie found on disk during an existing-library import.
///
/// Grouping the arguments into a record is what makes batching possible: the
/// import used to be one call and one transaction per title, which at 20,000
/// titles is 20,000 round trips to SQLite.
/// </summary>
public sealed record ExistingMovieImportRequest(
    string Title,
    int? ReleaseYear,
    string WantedStatus,
    string WantedReason,
    string? CurrentQuality,
    string? TargetQuality,
    bool QualityCutoffMet,
    string? FilePath,
    long? FileSizeBytes,
    PreferenceEvaluationSnapshot? PreferenceEvaluation = null,
    /// <summary>The film this is already known to be, when it is; see <c>MediaExistingImportRequest.MediaId</c>.</summary>
    string? MovieId = null);
