using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Series.Migrations;

/// <summary>
/// A show is in the library a person chose (#530). Adding one used to make it
/// wanted in every TV library. Existing shows keep the libraries holding their
/// episodes' files, or else their first library (the earliest created), and
/// episodes that pointed at a library the show left follow it.
/// </summary>
public sealed class V0041SeriesLibraryMembership : SqliteSqlMigration
{
    public override int Version => 41;

    public override string Name => "series_library_membership";

    protected override string Sql =>
        """
        DELETE FROM series_wanted_state
        WHERE has_file = 0
          AND EXISTS (
              SELECT 1 FROM series_wanted_state held
              WHERE held.series_id = series_wanted_state.series_id AND held.has_file = 1);

        DELETE FROM series_wanted_state
        WHERE has_file = 0
          AND library_id <> (
              SELECT MIN(first.library_id) FROM series_wanted_state first
              WHERE first.series_id = series_wanted_state.series_id);

        UPDATE episode_wanted_state
        SET library_id = (
            SELECT MIN(kept.library_id) FROM series_wanted_state kept
            WHERE kept.series_id = episode_wanted_state.series_id)
        WHERE NOT EXISTS (
                  SELECT 1 FROM series_wanted_state kept
                  WHERE kept.series_id = episode_wanted_state.series_id AND kept.library_id = episode_wanted_state.library_id)
          AND EXISTS (
                  SELECT 1 FROM series_wanted_state kept
                  WHERE kept.series_id = episode_wanted_state.series_id);
        """;
}
