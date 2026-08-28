using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Series.Migrations;

/// <summary>
/// What a show is filed under, so <i>The Bear</i> sits under <b>B</b>.
///
/// <para><b>Why a column and not an expression.</b> An expression index only
/// serves an <c>ORDER BY</c> that matches it character for character, and the
/// shelf's index is built on <c>lower(title)</c>. Changing the ordering in the
/// query alone would drop the sort off its index and turn every page of a
/// twenty-thousand-title library into a full scan and sort.</para>
///
/// <para><b>Why a trigger.</b> This is derived, and a derived value's danger is
/// a write path that forgets it. Titles are written by the import, by metadata
/// refreshes, by manual edits and by the catalogue sync; a trigger cannot be
/// forgotten by code that does not know it exists. Same reasoning as V0017.</para>
///
/// <para>The rule itself is <see cref="SortTitle"/> and is interpolated here
/// rather than written out, so SQLite and C# cannot drift apart.</para>
/// </summary>
public sealed class V0022SeriesSortTitle : SqliteSqlMigration
{
    public override int Version => 22;

    public override string Name => "series_sort_title";

    protected override string Sql =>
        $"""
        ALTER TABLE series_entries ADD COLUMN sort_title TEXT NULL;

        UPDATE series_entries SET sort_title = {SortTitle.SqlExpression("title")};

        CREATE INDEX IF NOT EXISTS ix_series_entries_sort_title_id
            ON series_entries (sort_title, id);

        CREATE TRIGGER IF NOT EXISTS trg_series_sort_title_ai
        AFTER INSERT ON series_entries
        BEGIN
            UPDATE series_entries
            SET sort_title = {SortTitle.SqlExpression("NEW.title")}
            WHERE id = NEW.id;
        END;

        CREATE TRIGGER IF NOT EXISTS trg_series_sort_title_au
        AFTER UPDATE OF title ON series_entries
        BEGIN
            UPDATE series_entries
            SET sort_title = {SortTitle.SqlExpression("NEW.title")}
            WHERE id = NEW.id;
        END;
        """;
}
