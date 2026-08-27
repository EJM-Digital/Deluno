using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// A saved view remembers *both* axes of the library filter.
///
/// Monitoring used to be a value of <c>quick_filter</c>, alongside the states —
/// which made it mutually exclusive with every one of them, so a view could
/// never be "missing, and unmonitored". Splitting the axes gave monitoring its
/// own control, and a saved view that dropped it would be lying about coming
/// back to the same view.
///
/// <c>NULL</c> and <c>'any'</c> both mean "either", so every view saved before
/// this keeps working and keeps meaning what it meant.
/// </summary>
public sealed class V0026LibraryViewMonitoringFilter : SqliteSqlMigration
{
    public override int Version => 26;

    public override string Name => "library_view_monitoring_filter";

    protected override string Sql =>
        """
        ALTER TABLE library_views ADD COLUMN monitoring TEXT NULL;

        -- Views saved while monitoring was a status keep their meaning: the
        -- filter moves to the axis it belongs on, and the status goes back to
        -- 'all' rather than being left as a word the state filter cannot read.
        UPDATE library_views
           SET monitoring = quick_filter,
               quick_filter = 'all'
         WHERE quick_filter IN ('monitored', 'unmonitored');
        """;
}
