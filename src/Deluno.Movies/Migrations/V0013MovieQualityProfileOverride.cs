using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Movies.Migrations;

/// <summary>
/// The per-title quality profile the code has always written and the table has
/// never had.
///
/// <c>SqliteMovieCatalogRepository.UpdateQualityProfileAsync</c> issues
/// <c>UPDATE movie_entries SET quality_profile_id = …</c>, and no migration ever
/// created that column. Every caller therefore threw
/// <c>SQLite Error 1: 'no such column: quality_profile_id'</c>:
///
/// <list type="bullet">
/// <item><c>POST /api/movies/bulk/quality-profile</c> — assigning a profile to
/// selected titles from the library returned 500.</item>
/// <item>Import-list sync — a source configured with a quality profile counted
/// every entry as both added and errored, because the add succeeded and the
/// profile write that followed it threw into the per-entry catch.</item>
/// </list>
///
/// Nullable with no default: null means the title follows its library's
/// profile, which is the behaviour every existing row already has.
/// </summary>
public sealed class V0013MovieQualityProfileOverride : SqliteSqlMigration
{
    public override int Version => 13;

    public override string Name => "movie_quality_profile_override";

    protected override string Sql =>
        """
        ALTER TABLE movie_entries ADD COLUMN quality_profile_id TEXT NULL;
        """;
}
