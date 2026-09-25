using Deluno.Quality.ReleasePreferences;

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
    IReadOnlyList<ImportedEpisodeItem>? Episodes,
    PreferenceEvaluationSnapshot? PreferenceEvaluation = null,
    IReadOnlyList<ImportedEpisodeNumberingItem>? AlternateEpisodes = null,
    IReadOnlyList<ImportedSeasonPackItem>? SeasonPacks = null,
    IReadOnlyList<PreferenceEvaluationSnapshot>? PreferenceEvaluations = null,
    /// <summary>The show this is already known to be, when it is; see <c>MediaExistingImportRequest.MediaId</c>.</summary>
    string? SeriesId = null,

    /// <summary>
    /// The library's own upgrade policy, needed to decide each episode's own
    /// wanted state rather than writing every held episode <c>covered</c>
    /// regardless of its quality (#654). Defaults to <c>false</c> so a caller
    /// that does not pass it keeps today's settle-immediately behaviour
    /// instead of silently gaining upgrade search traffic it never asked for.
    /// </summary>
    bool UpgradeUntilCutoff = false,

    /// <summary>Mirrors <see cref="UpgradeUntilCutoff"/> for episodes whose quality could not be read.</summary>
    bool UpgradeUnknownItems = false,

    /// <summary>
    /// What actually put this file here, for the episode's wanted reason:
    /// <c>"existing-library"</c> for a rescan of files already on disk, or
    /// the default for a completed download (a season pack episode says so
    /// itself via <see cref="ImportedEpisodeItem.FromSeasonPack"/>).
    /// </summary>
    string EpisodeImportSource = "download");
