using Deluno.Contracts;

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
    int? AttemptCount = null,

    // Normalised failure surfaced by the dispatch read model. This is derived
    // from the typed grab response when present, with a legacy-column fallback
    // for older rows.
    IntegrationFailure? Failure = null,

    // When the client said it had finished with the download, successfully or
    // not. DetectedUtc is when the client first showed it (#554); the wait for
    // an import is measured from here, so a long download is not an overdue one.
    DateTimeOffset? ClientCompletedUtc = null,

    // The release-selection decision recorded at grab time (#796): the quality
    // name and custom-format score the release was chosen for. Both are already
    // captured in decision telemetry for other purposes; this is the first place
    // that reads them back out for display.
    string? DecisionQuality = null,
    int? DecisionCustomFormatScore = null,

    // The release name read for language at grab time (#796), from the same
    // parser (Deluno.Quality.ReleasePreferences.ReleasePreferenceFactFactory)
    // that judges a probed file's audio track — never a second copy of the
    // token list in the web app.
    string? DecisionLanguage = null);
