using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Movies.Migrations;

/// <summary>
/// A movie the policy engine decided to leave alone stays left alone (#1065).
///
/// <para><b>The fault.</b> The policy engine deliberately stores
/// <c>covered</c> with <c>quality_cutoff_met = 0</c> when a profile does not
/// upgrade: a file of unknown quality with "upgrade unknown" off, or a file
/// below its cutoff with "upgrade until cutoff" off. That is a movie the user
/// told Deluno to stop looking for. V0045 read the pair as a contradiction and
/// rewrote it to <c>upgrade</c>, once in the migration and then on every write
/// through its triggers, so those movies turned gold, counted as Upgradable and
/// were picked up by the search for <c>wanted_status IN ('missing', 'upgrade')</c>.
/// That changed what an existing install does (#936).</para>
///
/// <para><b>Repair, once.</b> V0045 rewrote the status and left the reason, and
/// the reason is the engine's own words for its decision. A file-holding row
/// that is <c>upgrade</c> but whose reason is one of the engine's two "left
/// alone" sentences is put back on <c>covered</c>. Nothing is queued: only the
/// stored status moves, and a covered row is never searched. Each repaired movie
/// is written to <c>movie_rung_repairs</c> so the host can say so in Activity
/// once; this migration cannot, because Activity lives in another database.</para>
///
/// <para><b>Triggers, narrowed.</b> The two triggers no longer overrule the
/// engine about a file. They keep only the rule that cannot be argued with: a
/// title with no file is never <c>covered</c> or <c>upgrade</c>.</para>
///
/// <para>Rows V0045 turned into <c>missing</c> because they had no file are not
/// touched: once rewritten they are indistinguishable from a title that was
/// always missing, and putting them back on a rung that says "has a file" would
/// be a contradiction.</para>
/// </summary>
public sealed class V0047MovieQuietRungRestored : SqliteSqlMigration
{
    public override int Version => 47;

    public override string Name => "movie_quiet_rung_restored";

    /// <summary>The two sentences the policy engine writes when it stops looking beside a file below its cutoff.</summary>
    private const string LeftAlone =
        """
        has_file = 1
        AND quality_cutoff_met = 0
        AND wanted_status = 'upgrade'
        AND (
            wanted_reason = 'This movie is already in your library.'
            OR (wanted_reason LIKE 'This movie is currently %' AND wanted_reason NOT LIKE '%Deluno will%')
        )
        """;

    private const string NoFileRung =
        """
        CASE
            WHEN COALESCE((SELECT e.release_year FROM movie_entries e WHERE e.id = NEW.movie_id), 0)
                 > CAST(strftime('%Y', 'now') AS INTEGER) THEN 'upcoming'
            ELSE 'missing'
        END
        """;

    protected override string Sql =>
        $"""
        CREATE TABLE IF NOT EXISTS movie_rung_repairs (
            movie_id TEXT NOT NULL,
            library_id TEXT NOT NULL,
            repaired_utc TEXT NOT NULL,
            reported INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (movie_id, library_id)
        );

        INSERT OR IGNORE INTO movie_rung_repairs (movie_id, library_id, repaired_utc, reported)
        SELECT movie_id, library_id, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), 0
        FROM movie_wanted_state
        WHERE {LeftAlone};

        UPDATE movie_wanted_state
        SET wanted_status = 'covered'
        WHERE {LeftAlone};

        DROP TRIGGER IF EXISTS trg_movie_wanted_rung_ai;
        DROP TRIGGER IF EXISTS trg_movie_wanted_rung_au;

        CREATE TRIGGER trg_movie_wanted_rung_ai
        AFTER INSERT ON movie_wanted_state
        WHEN NEW.has_file = 0 AND NEW.wanted_status IN ('covered', 'upgrade')
        BEGIN
            UPDATE movie_wanted_state
            SET wanted_status = {NoFileRung}
            WHERE movie_id = NEW.movie_id AND library_id = NEW.library_id;
        END;

        CREATE TRIGGER trg_movie_wanted_rung_au
        AFTER UPDATE ON movie_wanted_state
        WHEN NEW.has_file = 0 AND NEW.wanted_status IN ('covered', 'upgrade')
        BEGIN
            UPDATE movie_wanted_state
            SET wanted_status = {NoFileRung}
            WHERE movie_id = NEW.movie_id AND library_id = NEW.library_id;
        END;
        """;
}
