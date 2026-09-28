using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Movies.Migrations;

/// <summary>
/// One stored rung per movie, and the two columns that describe it can no
/// longer disagree (#1047).
///
/// <para><b>The fault.</b> The movie filter chips were built from two
/// different questions: Quality met read <c>wanted_status = 'covered'</c> and
/// Upgradable read <c>has_file = 1 AND quality_cutoff_met = 0</c>. A movie
/// stored as <c>covered</c> beside a below-cutoff file was counted under both,
/// and drawn gold while listed as Upgradable, so Movies &gt; All said 20 while
/// the chips summed to 22. The chips now read the stored rung alone, exactly as
/// TV does; this migration makes that rung trustworthy.</para>
///
/// <para><b>Repair, once.</b> A file below its cutoff is <c>upgrade</c>, never
/// <c>covered</c>. A row that is <c>covered</c> or <c>upgrade</c> with no file
/// is recomputed: <c>upcoming</c> if the film is not out yet, otherwise
/// <c>missing</c> — the same words a title with no file has everywhere else.</para>
///
/// <para><b>And kept true.</b> Movie state is written from the workflow
/// service, the migration importer, the shared media-state repository, the
/// probe and several raw statements. Rather than trust each to agree, two
/// triggers apply the same rule to every insert and update, so no writer, now or
/// later, can leave the two columns disagreeing. They only ever rewrite a row
/// that is already contradictory.</para>
/// </summary>
public sealed class V0045MovieOneRungPerMovie : SqliteSqlMigration
{
    public override int Version => 45;

    public override string Name => "movie_one_rung_per_movie";

    /// <summary>The rung a contradictory row should be on, from the row itself.</summary>
    private const string Rung =
        """
        CASE
            WHEN {t}.has_file = 1 AND {t}.quality_cutoff_met = 0 THEN 'upgrade'
            WHEN {t}.has_file = 1 THEN 'covered'
            WHEN COALESCE((SELECT e.release_year FROM movie_entries e WHERE e.id = {t}.movie_id), 0)
                 > CAST(strftime('%Y', 'now') AS INTEGER) THEN 'upcoming'
            ELSE 'missing'
        END
        """;

    /// <summary>Only a row that says one thing about its file and another about its rung.</summary>
    private const string Contradiction =
        """
        (
            ({t}.has_file = 1 AND {t}.quality_cutoff_met = 0 AND {t}.wanted_status = 'covered')
            OR ({t}.has_file = 0 AND {t}.wanted_status IN ('covered', 'upgrade'))
        )
        """;

    protected override string Sql =>
        $"""
        UPDATE movie_wanted_state
        SET wanted_status = {Rung.Replace("{t}", "movie_wanted_state")}
        WHERE {Contradiction.Replace("{t}", "movie_wanted_state")};

        CREATE TRIGGER IF NOT EXISTS trg_movie_wanted_rung_ai
        AFTER INSERT ON movie_wanted_state
        WHEN {Contradiction.Replace("{t}", "NEW")}
        BEGIN
            UPDATE movie_wanted_state
            SET wanted_status = {Rung.Replace("{t}", "NEW")}
            WHERE movie_id = NEW.movie_id AND library_id = NEW.library_id;
        END;

        CREATE TRIGGER IF NOT EXISTS trg_movie_wanted_rung_au
        AFTER UPDATE ON movie_wanted_state
        WHEN {Contradiction.Replace("{t}", "NEW")}
        BEGIN
            UPDATE movie_wanted_state
            SET wanted_status = {Rung.Replace("{t}", "NEW")}
            WHERE movie_id = NEW.movie_id AND library_id = NEW.library_id;
        END;
        """;
}
