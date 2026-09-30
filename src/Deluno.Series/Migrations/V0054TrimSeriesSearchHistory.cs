using Deluno.Media;

namespace Deluno.Series.Migrations;

/// <summary>
/// #1083: every search row kept every release it compared (about 730 KB a row, 1.8 GB for two shows). Trims each to what the screens show, then compacts the file once.
/// </summary>
public sealed class V0054TrimSeriesSearchHistory : SearchHistoryTrimMigration
{
    public override int Version => 54;

    public override string Name => "trim_series_search_history";

    protected override string Table => "series_search_history";
}
