using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Movies.Migrations;

/// <summary>
/// What a film is filed under, so <i>The Matrix</i> sits under <b>M</b>.
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
public sealed class V0021MovieSortTitle : SqliteSqlMigration
{
    public override int Version => 21;

    public override string Name => "movie_sort_title";

    protected override string Sql =>
        $"""
        ALTER TABLE movie_entries ADD COLUMN sort_title TEXT NULL;

        UPDATE movie_entries SET sort_title = {SortTitle.SqlExpression("title")};

        CREATE INDEX IF NOT EXISTS ix_movie_entries_sort_title_id
            ON movie_entries (sort_title, id);

        CREATE TRIGGER IF NOT EXISTS trg_movie_sort_title_ai
        AFTER INSERT ON movie_entries
        BEGIN
            UPDATE movie_entries
            SET sort_title = {SortTitle.SqlExpression("NEW.title")}
            WHERE id = NEW.id;
        END;

        CREATE TRIGGER IF NOT EXISTS trg_movie_sort_title_au
        AFTER UPDATE OF title ON movie_entries
        BEGIN
            UPDATE movie_entries
            SET sort_title = {SortTitle.SqlExpression("NEW.title")}
            WHERE id = NEW.id;
        END;
        """;
}
