using Deluno.Media;

namespace Deluno.Movies.Migrations;

/// <summary>
/// #1083: every search row kept every release it compared. Trims each to what the screens show, then compacts the file once.
/// </summary>
public sealed class V0051TrimMovieSearchHistory : SearchHistoryTrimMigration
{
    public override int Version => 51;

    public override string Name => "trim_movie_search_history";

    protected override string Table => "movie_search_history";
}
