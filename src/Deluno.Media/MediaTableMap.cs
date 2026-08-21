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
    string RecoveryTable)
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
                "movie_import_recovery_cases"),
            MediaKind.Series => new(
                DelunoDatabaseNames.Series,
                "series_entries",
                "s",
                "start_year",
                "series_wanted_state",
                "series_id",
                "series_search_history",
                "series_import_recovery_cases"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
}
