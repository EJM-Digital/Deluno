using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Series.Migrations;

/// <summary>
/// How far through a show you are, on the show's own row, so a library can be
/// filtered and ordered by it.
///
/// <para><b>The thing that makes this different from every other cached fact.</b>
/// <c>V0017</c>'s size and quality change when something <i>happens</i> — a file
/// arrives, a profile is edited — so a trigger catches every one of them. How far
/// through a show you are changes because <b>time passed</b>. Nothing is written
/// when Thursday's episode airs, and the show quietly goes from 8 of 10 to 8 of
/// 11 with no row touched anywhere. A trigger cannot fire on that, and a stored
/// number with nothing watching it is wrong by the following morning.</para>
///
/// <para><b>So the expiry is stored beside the number.</b>
/// <c>next_air_date_utc</c> is the first episode still to come, and it is exactly
/// the moment these counts stop being true. While it is in the future the counts
/// cannot be wrong — which covers every finished show, and every running show
/// between episodes, which together is nearly the whole library. When it passes,
/// <c>ix_series_entries_progress_expiry</c> finds precisely the handful of shows
/// that have moved on, and only those are recomputed. A show that ended in 2013
/// is never looked at again.</para>
///
/// <para>James chose this over the two cheaper answers, and the cheap ones are
/// worth recording so nobody re-proposes them: recomputing on the library cycle
/// leaves a show that aired last night showing yesterday's figure — wrong
/// precisely for the shows somebody is most likely watching — and working it out
/// on every request means reading every episode of every show, which is the one
/// thing <c>#322</c> rule 1 forbids at twenty thousand titles.</para>
///
/// <para><b>The triggers still earn their place.</b> Time is not the only thing
/// that moves these numbers: an episode is imported, a season is added by a
/// catalogue sync, an air date is corrected. Those <i>are</i> writes, and a
/// trigger cannot be forgotten by a write path that does not know it exists —
/// which is the same reason <c>V0017</c> gives.</para>
/// </summary>
public sealed class V0020SeriesProgressFacts : SqliteSqlMigration
{
    public override int Version => 20;

    public override string Name => "series_progress_facts";

    protected override string Sql =>
        $"""
        -- Continuing, Ended, Canceled. Arrives in the metadata blob and has
        -- never had a column, so a show that finished five years ago and is
        -- short three episodes has been indistinguishable from one still airing
        -- them — which is the difference between a gap you can close and no gap
        -- at all.
        ALTER TABLE series_entries ADD COLUMN status TEXT NULL;

        -- Time-independent: how many episodes exist, and how many of those are
        -- held. Neither depends on the clock, so the triggers below are the
        -- whole of their maintenance.
        ALTER TABLE series_entries ADD COLUMN episode_count INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE series_entries ADD COLUMN episode_with_file_count INTEGER NOT NULL DEFAULT 0;

        -- Time-dependent: true as at the moment they were computed, and provably
        -- still true for as long as next_air_date_utc has not passed.
        ALTER TABLE series_entries ADD COLUMN aired_episode_count INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE series_entries ADD COLUMN aired_with_file_count INTEGER NOT NULL DEFAULT 0;

        -- The expiry, and the reason this design works. NULL means nothing is
        -- still to come, so the counts above never go stale on their own.
        ALTER TABLE series_entries ADD COLUMN next_air_date_utc TEXT NULL;

        -- A whole season with nothing in it, which is a different and worse
        -- problem from a few scattered gaps. Counted over seasons that have
        -- aired at all, and never over season zero: a show with no specials is
        -- not a show missing a season.
        ALTER TABLE series_entries ADD COLUMN has_missing_season INTEGER NOT NULL DEFAULT 0;

        CREATE INDEX IF NOT EXISTS ix_series_entries_status_id
            ON series_entries (COALESCE(status, ''), id);

        CREATE INDEX IF NOT EXISTS ix_series_entries_next_air_id
            ON series_entries (next_air_date_utc, id);

        CREATE INDEX IF NOT EXISTS ix_series_entries_progress_id
            ON series_entries (aired_with_file_count, aired_episode_count, id);

        -- The one the expiry sweep seeks on. Partial, because a show with
        -- nothing still to come can never expire and there is no reason to walk
        -- past it.
        CREATE INDEX IF NOT EXISTS ix_series_entries_progress_expiry
            ON series_entries (next_air_date_utc)
            WHERE next_air_date_utc IS NOT NULL;

        CREATE INDEX IF NOT EXISTS ix_series_entries_missing_season_id
            ON series_entries (has_missing_season, id);

        -- Backfill what is already known. `status` comes out of the metadata
        -- blob where it has been sitting unread; a blob written before the
        -- broker learnt to send it has no such key, and those rows stay NULL
        -- until a metadata refresh — the same bargain #326 makes about artwork.
        UPDATE series_entries
        SET status = json_extract(metadata_json, '$.Status')
        WHERE metadata_json IS NOT NULL
          AND json_valid(metadata_json)
          AND json_extract(metadata_json, '$.Status') IS NOT NULL;

        -- Computed here, once, rather than by expiring every show and letting
        -- the first person who filters a shelf pay for the whole library. That
        -- was the first cut and the twenty-thousand-title benchmark caught it:
        -- one migration turned every subsequent filtered page into a full
        -- recompute, which is precisely the thundering herd this design exists
        -- to avoid.
        --
        -- SQLite's own clock, used exactly once. A second clock is only a
        -- hazard when two of them disagree over time; a one-off backfill has no
        -- later to disagree in, and the alternative is shipping a library-wide
        -- stall to every existing install.

        -- The triggers do not count anything. They mark the show as expired and
        -- leave the arithmetic to the sweep.
        --
        -- That is deliberate: half of what is counted here depends on the clock,
        -- and a trigger has no way to ask Deluno what time it is. It would have
        -- to call SQLite's own `now`, which is a second clock in a codebase whose
        -- every defect so far has been one rule written twice in places that
        -- could not check each other. Expiring the row costs one write and hands
        -- the question to the one caller that holds the real clock.
        {Backfill}

        CREATE TRIGGER IF NOT EXISTS trg_series_progress_ai
        AFTER INSERT ON episode_entries
        BEGIN
            UPDATE series_entries SET next_air_date_utc = '0001-01-01T00:00:00.0000000+00:00' WHERE id = NEW.series_id;
        END;

        CREATE TRIGGER IF NOT EXISTS trg_series_progress_au
        AFTER UPDATE ON episode_entries
        BEGIN
            UPDATE series_entries SET next_air_date_utc = '0001-01-01T00:00:00.0000000+00:00' WHERE id = NEW.series_id;
        END;

        CREATE TRIGGER IF NOT EXISTS trg_series_progress_ad
        AFTER DELETE ON episode_entries
        BEGIN
            UPDATE series_entries SET next_air_date_utc = '0001-01-01T00:00:00.0000000+00:00' WHERE id = OLD.series_id;
        END;
        """;

    /// <summary>
    /// A date that has definitely passed, written into
    /// <c>next_air_date_utc</c> to mean "these counts are no longer true".
    ///
    /// <para>An ordinary date rather than a flag or an empty string, so nothing
    /// anywhere needs to special-case it: every comparison already asks whether
    /// the expiry has passed, and this one always has.</para>
    /// </summary>
    public const string Expired = "0001-01-01T00:00:00.0000000+00:00";

    /// <summary>
    /// Everything a show's progress row holds, worked out from its episodes.
    ///
    /// <para><b>Written once and used from one place.</b> The sweep is the only
    /// caller — the triggers deliberately do not count, so this arithmetic never
    /// gets a second copy in a trigger body where nothing could hold the two
    /// together.</para>
    ///
    /// <para><c>@now</c> is bound by the caller from Deluno's own
    /// <c>TimeProvider</c>, formatted the way <c>air_date_utc</c> is written —
    /// a <c>DateTimeOffset</c> renders its offset as <c>+00:00</c> and a
    /// <c>DateTime</c> renders it as <c>Z</c>, and these are compared as
    /// text.</para>
    /// </summary>
    /// <summary>
    /// The sweep: everything that has gone stale since anybody last looked, and
    /// nothing else. <c>@now</c> is bound by the caller from Deluno's own
    /// <c>TimeProvider</c>.
    /// </summary>
    /// <summary>
    /// SQLite's own clock, rendered the way <c>air_date_utc</c> is stored — with
    /// a <c>+00:00</c> offset rather than a <c>Z</c>, because these are compared
    /// as text. Used once, by the backfill below, and nowhere else ever.
    /// </summary>
    private const string SqliteNow = "strftime('%Y-%m-%dT%H:%M:%f0000+00:00','now')";

    /// <summary>The one-off backfill: the sweep's arithmetic, over every row.</summary>
    private static readonly string Backfill = RecomputeBody.Replace("@now", SqliteNow, StringComparison.Ordinal) + ";";

    public const string RecomputeSql =
        RecomputeBody + " WHERE next_air_date_utc IS NOT NULL AND next_air_date_utc <= @now;";

    /// <summary>
    /// The arithmetic on its own, without a WHERE, so the one-off backfill in
    /// this migration and the sweep that runs for ever after are the same
    /// counting rule rather than two copies of it that could drift.
    /// </summary>
    private const string RecomputeBody =
        """
        UPDATE series_entries SET
            episode_count = (
                SELECT COUNT(*) FROM episode_entries e WHERE e.series_id = series_entries.id
            ),
            episode_with_file_count = (
                SELECT COUNT(*) FROM episode_entries e
                WHERE e.series_id = series_entries.id AND e.has_file = 1
            ),
            aired_episode_count = (
                SELECT COUNT(*) FROM episode_entries e
                WHERE e.series_id = series_entries.id
                  AND e.air_date_utc IS NOT NULL AND e.air_date_utc <= @now
            ),
            aired_with_file_count = (
                SELECT COUNT(*) FROM episode_entries e
                WHERE e.series_id = series_entries.id
                  AND e.air_date_utc IS NOT NULL AND e.air_date_utc <= @now
                  AND e.has_file = 1
            ),
            -- NULL when nothing is still to come, which is what makes a finished
            -- show cost nothing ever again.
            next_air_date_utc = (
                SELECT MIN(e.air_date_utc) FROM episode_entries e
                WHERE e.series_id = series_entries.id AND e.air_date_utc > @now
            ),
            -- Season zero is specials. A show with no specials is not a show
            -- missing a season, and counting it would put a red mark on most of
            -- the library.
            has_missing_season = (
                SELECT CASE WHEN EXISTS (
                    SELECT 1 FROM episode_entries e
                    WHERE e.series_id = series_entries.id
                      AND e.season_number > 0
                      AND e.air_date_utc IS NOT NULL AND e.air_date_utc <= @now
                    GROUP BY e.season_number
                    HAVING SUM(CASE WHEN e.has_file = 1 THEN 1 ELSE 0 END) = 0
                ) THEN 1 ELSE 0 END
            )
        """;
}
