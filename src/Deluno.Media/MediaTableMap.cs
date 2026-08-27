using Deluno.Infrastructure.Storage;

namespace Deluno.Media;

/// <summary>
/// The two catalogues share wanted-state semantics but not table names. Keeping
/// the identifiers in one allow-listed map makes the shared SQL explicit and
/// prevents interpolating caller input into a query.
/// </summary>
public sealed record MediaTableMap(
    string DatabaseName,
    string EntryTable,
    string EntryAlias,
    string YearColumn,
    string WantedTable,
    string WantedMediaIdColumn,
    string HistoryTable,
    string RecoveryTable,
    /// <summary>
    /// The columns that say whether a title is out yet, joined onto its wanted
    /// state — three release dates and the availability rule for a movie, and for
    /// a show whether any episode has aired.
    ///
    /// Selected rather than decided in SQL, because the rule for a movie is
    /// <see cref="Deluno.Contracts.MovieAvailability"/> and writing it a second
    /// time in a WHERE clause is how two copies of a rule start disagreeing.
    /// </summary>
    string ReleaseColumns,
    string ReleaseJoin,
    /// <summary>
    /// Where subtitle state lives, and what it hangs off.
    ///
    /// The one asymmetry in this map is a fact about the domain rather than a
    /// copy: a movie's subtitle belongs to the movie, an episode's belongs to
    /// the episode. Everything else — the columns, the upsert, the rollup — is
    /// one SQL body reading these names, which is what keeps ADR-001's Step 2
    /// from inheriting a second hand-written pair.
    /// </summary>
    string SubtitleTable,
    string SubtitleMediaIdColumn,
    string SubtitleScanTable,
    /// <summary>
    /// What a subtitle row rolls up to on the catalogue page. A movie is its
    /// own title; an episode's subtitles belong to its show, and only count
    /// while the episode has a file and has aired — the same two conditions
    /// <c>AiredWithFileCount</c> uses, so a bar can never read past what was
    /// asked for.
    /// </summary>
    string SubtitleRollupIdColumn,
    string SubtitleRollupJoin,
    /// <summary>
    /// The file a subtitle scan reads, and how it reaches a library. For movies
    /// the wanted state carries both; for episodes the path is on the episode
    /// and the library is on the show's wanted state.
    /// </summary>
    string SubtitleFileSource,
    string SubtitleFileIdColumn,
    string SubtitleFileLibraryJoin,
    string SubtitleFileLibraryFilter)
{
    public static MediaTableMap For(MediaKind kind)
        => kind switch
        {
            MediaKind.Movie => new(
                DelunoDatabaseNames.Movies,
                "movie_entries",
                "m",
                "release_year",
                "movie_wanted_state",
                "movie_id",
                "movie_search_history",
                "movie_import_recovery_cases",
                "e.in_cinemas_date, e.digital_release_date, e.physical_release_date, e.minimum_availability, NULL",
                "JOIN movie_entries e ON e.id = w.movie_id",
                "movie_subtitle_state",
                "movie_id",
                "movie_subtitle_scan",
                "sub.movie_id",
                "",
                "movie_wanted_state f",
                "f.movie_id",
                "",
                "f.library_id = @libraryId"),
            MediaKind.Series => new(
                DelunoDatabaseNames.Series,
                "series_entries",
                "s",
                "start_year",
                "series_wanted_state",
                "series_id",
                "series_search_history",
                "series_import_recovery_cases",
                // A show is out once any episode has aired. No catalogued
                // episodes at all is not evidence it has not — an unsynced show
                // would otherwise stop being searched for.
                "NULL, NULL, NULL, NULL, (SELECT MIN(ep.air_date_utc) FROM episode_entries ep WHERE ep.series_id = w.series_id)",
                "",
                "episode_subtitle_state",
                "episode_id",
                "episode_subtitle_scan",
                "rollup.series_id",
                """
                JOIN episode_entries rollup
                    ON rollup.id = sub.episode_id
                   AND rollup.has_file = 1
                   AND rollup.air_date_utc IS NOT NULL
                   AND rollup.air_date_utc <= @now
                """,
                "episode_entries f",
                "f.id",
                "JOIN series_wanted_state lib ON lib.series_id = f.series_id AND lib.library_id = @libraryId",
                "1 = 1"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
}
