using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Adds a day-of-week axis to a library's search window (#832), so searches
/// can be paused entirely on days that do not suit — a connection that is
/// busy for other reasons on a given day, say — alongside the existing
/// hour-of-day window.
///
/// <para>Stored as a 7-bit mask, one bit per <see cref="System.DayOfWeek"/>
/// value (Sunday = bit 0 ... Saturday = bit 6). 127 (all bits set) means
/// every day, which is what every existing library gets so nothing changes
/// for it until a day is actually deselected.</para>
/// </summary>
public sealed class V0098LibrarySearchWindowDays : SqliteSqlMigration
{
    public override int Version => 98;

    public override string Name => "library_search_window_days";

    protected override string Sql =>
        "ALTER TABLE libraries ADD COLUMN search_window_days INTEGER NOT NULL DEFAULT 127;";
}
