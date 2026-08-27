using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Movies.Migrations;

/// <summary>
/// Size and quality, on the movie row, so a library can be ordered by them.
///
/// <para><b>Why this exists.</b> A file's size and quality live on
/// <c>movie_wanted_state</c>, which the catalogue page reaches through a
/// correlated pick — <c>ws.rowid = (SELECT … LIMIT 1)</c>. SQLite cannot index
/// that, so ordering by a column on its far side means running the pick for
/// every title in the library and sorting the lot: a full scan wearing a seek's
/// clothes, fine at eleven titles and ruinous at twenty thousand, with nothing
/// about the result looking wrong. These two columns are how the sort becomes an
/// index walk instead.</para>
///
/// <para><b>Why a trigger rather than a repository write.</b> This is a derived
/// value, and the thing that makes derived values dangerous is a write path that
/// forgets to update them. There are several that touch wanted state — import,
/// quality recalculation, marking a file missing, the shared media store — and
/// there will be more. A trigger cannot be forgotten by code that does not know
/// it exists.</para>
///
/// <para><b>The one real risk, and its guard.</b> The pick order below is the
/// same order <c>CatalogueWantedState.Join</c> uses, and it has to stay that
/// way: if they disagreed, a page would sort by one file's size and display
/// another's. That is now a rule written in two languages, so
/// <c>CatalogueSortableFactsTests</c> asserts the two agree, and it fails if
/// either moves.</para>
/// </summary>
public sealed class V0016MovieSortableFileFacts : SqliteSqlMigration
{
    public override int Version => 16;

    public override string Name => "movie_sortable_file_facts";

    protected override string Sql =>
        """
        -- The quality ladder, as data, because SQL cannot ask C# what a tier is
        -- worth. Seeded with the shipped ladder and re-synced whenever somebody
        -- edits the quality model, so a renamed or re-ranked tier re-sorts the
        -- library rather than quietly ordering by a stale number.
        CREATE TABLE IF NOT EXISTS quality_ranks (
            name TEXT PRIMARY KEY COLLATE NOCASE,
            rank INTEGER NOT NULL
        );

        INSERT OR REPLACE INTO quality_ranks (name, rank) VALUES
            ('Unknown', 0), ('WORKPRINT', 1), ('CAM', 2), ('TELESYNC', 4), ('TELECINE', 5),
            ('REGIONAL', 6), ('DVDSCR', 7), ('SDTV', 10), ('DVD', 20), ('DVD-R', 21),
            ('WEB 480p', 22), ('Bluray 480p', 24), ('Bluray 576p', 25), ('HDTV 720p', 30),
            ('WEB 720p', 40), ('Bluray 720p', 50), ('HDTV 1080p', 60), ('WEB 1080p', 70),
            ('Bluray 1080p', 80), ('Remux 1080p', 90), ('HDTV 2160p', 95), ('WEB 2160p', 100),
            ('Bluray 2160p', 110), ('Remux 2160p', 120), ('BR-DISK', 125), ('Raw-HD', 126);

        ALTER TABLE movie_entries ADD COLUMN primary_file_size_bytes INTEGER NULL;
        ALTER TABLE movie_entries ADD COLUMN primary_quality_rank INTEGER NULL;

        CREATE INDEX IF NOT EXISTS ix_movie_entries_size_id
            ON movie_entries (COALESCE(primary_file_size_bytes, -1), id);

        CREATE INDEX IF NOT EXISTS ix_movie_entries_quality_rank_id
            ON movie_entries (COALESCE(primary_quality_rank, -1), id);

        -- Bitrate, which Radarr and Sonarr cannot sort by at all.
        --
        -- Size alone says a file is big; size over runtime says whether it is
        -- big *for what it is*. That is the question behind every "why is this
        -- 2160p file only 4 GB" and every over-large remux nobody wanted, and it
        -- is the one a person actually asks when auditing a library.
        --
        -- An expression index rather than another cached column: both inputs are
        -- already maintained on this row, so there is nothing extra to keep
        -- true. The ORDER BY has to spell the expression identically, which is
        -- why it lives in CatalogueKeyset and not here.
        CREATE INDEX IF NOT EXISTS ix_movie_entries_bitrate_id
            ON movie_entries (COALESCE(CAST(primary_file_size_bytes AS REAL) / NULLIF(runtime_minutes, 0), -1), id);

        -- Everything already in the library, so the columns are true the moment
        -- the migration finishes rather than the next time a file changes.
        UPDATE movie_entries SET
            primary_file_size_bytes = (
                SELECT pick.file_size_bytes FROM movie_wanted_state pick
                WHERE pick.movie_id = movie_entries.id
                ORDER BY pick.has_file DESC, pick.quality_cutoff_met ASC, pick.library_id ASC
                LIMIT 1
            ),
            primary_quality_rank = (
                SELECT (SELECT r.rank FROM quality_ranks r WHERE r.name = pick.current_quality)
                FROM movie_wanted_state pick
                WHERE pick.movie_id = movie_entries.id
                ORDER BY pick.has_file DESC, pick.quality_cutoff_met ASC, pick.library_id ASC
                LIMIT 1
            );

        CREATE TRIGGER IF NOT EXISTS trg_movie_primary_facts_ai
        AFTER INSERT ON movie_wanted_state
        BEGIN
            UPDATE movie_entries SET
                primary_file_size_bytes = (
                    SELECT pick.file_size_bytes FROM movie_wanted_state pick
                    WHERE pick.movie_id = NEW.movie_id
                    ORDER BY pick.has_file DESC, pick.quality_cutoff_met ASC, pick.library_id ASC
                    LIMIT 1
                ),
                primary_quality_rank = (
                    SELECT (SELECT r.rank FROM quality_ranks r WHERE r.name = pick.current_quality)
                    FROM movie_wanted_state pick
                    WHERE pick.movie_id = NEW.movie_id
                    ORDER BY pick.has_file DESC, pick.quality_cutoff_met ASC, pick.library_id ASC
                    LIMIT 1
                )
            WHERE id = NEW.movie_id;
        END;

        CREATE TRIGGER IF NOT EXISTS trg_movie_primary_facts_au
        AFTER UPDATE ON movie_wanted_state
        BEGIN
            UPDATE movie_entries SET
                primary_file_size_bytes = (
                    SELECT pick.file_size_bytes FROM movie_wanted_state pick
                    WHERE pick.movie_id = NEW.movie_id
                    ORDER BY pick.has_file DESC, pick.quality_cutoff_met ASC, pick.library_id ASC
                    LIMIT 1
                ),
                primary_quality_rank = (
                    SELECT (SELECT r.rank FROM quality_ranks r WHERE r.name = pick.current_quality)
                    FROM movie_wanted_state pick
                    WHERE pick.movie_id = NEW.movie_id
                    ORDER BY pick.has_file DESC, pick.quality_cutoff_met ASC, pick.library_id ASC
                    LIMIT 1
                )
            WHERE id = NEW.movie_id;
        END;

        CREATE TRIGGER IF NOT EXISTS trg_movie_primary_facts_ad
        AFTER DELETE ON movie_wanted_state
        BEGIN
            UPDATE movie_entries SET
                primary_file_size_bytes = (
                    SELECT pick.file_size_bytes FROM movie_wanted_state pick
                    WHERE pick.movie_id = OLD.movie_id
                    ORDER BY pick.has_file DESC, pick.quality_cutoff_met ASC, pick.library_id ASC
                    LIMIT 1
                ),
                primary_quality_rank = (
                    SELECT (SELECT r.rank FROM quality_ranks r WHERE r.name = pick.current_quality)
                    FROM movie_wanted_state pick
                    WHERE pick.movie_id = OLD.movie_id
                    ORDER BY pick.has_file DESC, pick.quality_cutoff_met ASC, pick.library_id ASC
                    LIMIT 1
                )
            WHERE id = OLD.movie_id;
        END;
        """;
}
