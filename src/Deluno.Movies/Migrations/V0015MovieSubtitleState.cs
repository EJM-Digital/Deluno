using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Movies.Migrations;

/// <summary>
/// What subtitles Deluno holds for a movie, and when it last looked.
///
/// The twin of <c>V0016SeriesSubtitleState</c>, and deliberately the same shape
/// down to the column names: ADR-001 records that Movies and Series are
/// parallel copies whose duplication is actively reproducing, and its Step 2 is
/// to merge them. One SQL body in <c>MediaTableMap</c> reads and writes both,
/// so this pair costs that merge two table names rather than two
/// implementations.
///
/// The one difference is a fact about the domain rather than a copy: a movie's
/// subtitle belongs to the movie, an episode's belongs to the episode. That is
/// also what makes a show's bar the sum over the episodes it holds.
///
/// <para><b>Why a row per variant.</b> The key carries <c>forced</c> and
/// <c>hearing_impaired</c> because they are different subtitles, not flavours of
/// one. A file whose only English track is forced has English for four lines of
/// Elvish; counting it as English coverage tells somebody they are done when
/// they are not.</para>
///
/// <para><b>Why the scan table.</b> Reading a file's subtitles costs a
/// directory listing and an ffprobe. Doing that once per file, and again only
/// when the file changes, is the difference between a background pass nobody
/// notices and one that re-probes twenty thousand files every cycle.
/// <c>probe_status</c> is kept because "no embedded tracks" and "ffprobe was
/// not installed" are different facts.</para>
/// </summary>
public sealed class V0015MovieSubtitleState : SqliteSqlMigration
{
    public override int Version => 15;

    public override string Name => "movie_subtitle_state";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS movie_subtitle_state (
            movie_id TEXT NOT NULL,
            language TEXT NOT NULL,
            forced INTEGER NOT NULL DEFAULT 0,
            hearing_impaired INTEGER NOT NULL DEFAULT 0,
            source TEXT NOT NULL,
            file_path TEXT NULL,
            stream_index INTEGER NULL,
            codec TEXT NULL,
            provider TEXT NULL,
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL,
            PRIMARY KEY (movie_id, language, forced, hearing_impaired),
            FOREIGN KEY (movie_id) REFERENCES movie_entries(id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS movie_subtitle_scan (
            movie_id TEXT PRIMARY KEY,
            file_path TEXT NOT NULL,
            file_size_bytes INTEGER NULL,
            probe_status TEXT NOT NULL,
            subtitle_count INTEGER NOT NULL DEFAULT 0,
            scanned_utc TEXT NOT NULL,
            FOREIGN KEY (movie_id) REFERENCES movie_entries(id) ON DELETE CASCADE
        );
        """;
}
