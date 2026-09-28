using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// #984: the library search interval becomes configurable down to minutes,
/// not whole hours only. Huntarr expresses its hunt interval in minutes;
/// Deluno's own "Every N h" control could never ask for 15 or 30 minutes.
///
/// <para>A new <c>search_interval_minutes</c> column is the value every
/// reader and writer uses from here on. It is backfilled from the existing
/// <c>search_interval_hours</c> column (hours × 60) so every existing
/// library keeps exactly the cadence it already had — an install running
/// "every 6 hours" reads "every 360 minutes" after this runs, which is the
/// same schedule, not a changed one. The old column is left in place,
/// unread, rather than dropped: SQLite migrations here are additive, and
/// dropping a column a running install has already written is not worth the
/// risk for a column nothing reads any more.</para>
/// </summary>
public sealed class V0115LibrarySearchIntervalMinutes : SqliteSqlMigration
{
    public override int Version => 115;

    public override string Name => "library_search_interval_minutes";

    protected override string Sql =>
        """
        ALTER TABLE libraries ADD COLUMN search_interval_minutes INTEGER NOT NULL DEFAULT 360;
        UPDATE libraries SET search_interval_minutes = search_interval_hours * 60;
        """;
}
