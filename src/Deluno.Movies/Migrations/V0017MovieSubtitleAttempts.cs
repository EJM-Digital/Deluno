using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Movies.Migrations;

/// <summary>
/// When Deluno last went looking for a subtitle it did not find, and when it may
/// look again.
///
/// <para><b>Why this is not optional.</b> Without it the search has no memory.
/// <c>ListWantedAsync</c> takes the first <c>MaxItemsPerRun</c> rows that are
/// short of a language, in whatever order SQLite hands them back — so a library
/// where five thousand films have no Japanese subtitle asks the same ten films
/// every cycle, for ever, and the other four thousand nine hundred and ninety
/// are never asked at all. Nothing about that looks wrong: the job succeeds, the
/// providers answer, and the bar never moves.</para>
///
/// <para><b>It is the release search's vocabulary, deliberately.</b>
/// <c>last_search_utc</c> and <c>next_eligible_search_utc</c> are the same two
/// columns <c>movie_wanted_state</c> already carries for releases, and the delay
/// is the library's own <c>RetryDelayHours</c>. DESIGN-002 asked for exactly
/// this — backoff that "reads the same way rather than inventing a second
/// vocabulary" — and it means the filter that can already ask "not searched in
/// ninety days" needs no second idea of what searching means.</para>
///
/// <para><b>A row here means a failure, not a subtitle.</b> Success writes to
/// <c>movie_subtitle_state</c> and deletes from here, so this table only ever
/// holds outstanding work and stays small.</para>
///
/// <para><b>No permanent skip.</b> Weir's Subber had one, and it is the
/// wrong shape: a title that can never be asked again is work that has silently
/// left the system, and nobody finds out when somebody finally uploads the
/// subtitle. The delay doubles and then stops doubling, so a hopeless title
/// costs one request a fortnight for ever — which is nothing — and still
/// succeeds the day it becomes possible.</para>
/// </summary>
public sealed class V0017MovieSubtitleAttempts : SqliteSqlMigration
{
    public override int Version => 17;

    public override string Name => "movie_subtitle_attempts";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS movie_subtitle_attempt (
            movie_id TEXT NOT NULL,
            language TEXT NOT NULL,
            attempts INTEGER NOT NULL DEFAULT 0,
            last_search_utc TEXT NOT NULL,
            next_eligible_search_utc TEXT NOT NULL,
            last_result TEXT NULL,
            PRIMARY KEY (movie_id, language),
            FOREIGN KEY (movie_id) REFERENCES movie_entries(id) ON DELETE CASCADE
        );

        -- The slice reads "what is due, oldest first", so that is the index. A
        -- library that is a long way behind rotates through itself instead of
        -- asking the same ten films every night.
        CREATE INDEX IF NOT EXISTS ix_movie_subtitle_attempt_due
            ON movie_subtitle_attempt (next_eligible_search_utc, movie_id);
        """;
}
