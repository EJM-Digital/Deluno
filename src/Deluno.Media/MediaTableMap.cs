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
    /// state — three release dates and the availability rule for a film, and for
    /// a show whether any episode has aired.
    ///
    /// Selected rather than decided in SQL, because the rule for a film is
    /// <see cref="Deluno.Contracts.MovieAvailability"/> and writing it a second
    /// time in a WHERE clause is how two copies of a rule start disagreeing.
    /// </summary>
    string ReleaseColumns,
    string ReleaseJoin)
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
                "JOIN movie_entries e ON e.id = w.movie_id"),
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
                ""),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
}
