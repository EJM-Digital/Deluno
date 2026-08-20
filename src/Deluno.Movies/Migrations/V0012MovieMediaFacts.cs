using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Movies.Migrations;

/// <summary>
/// The facts the library list has always displayed but never had.
///
/// Sorting and searching on codec, audio, release group, runtime, path and
/// popularity were all offered by the interface and all silently did nothing,
/// because the values were read from a provider metadata blob that never
/// carried them. Two different sources fill that gap, and they belong in
/// different places:
///
/// <list type="bullet">
/// <item><c>runtime_minutes</c>, <c>popularity</c> and <c>vote_count</c>
/// describe the <em>title</em> and come from the metadata provider, so they sit
/// on the entry.</item>
/// <item>Codec, audio and release group describe the <em>file</em> and are read
/// from its name, so they sit on the wanted state next to the path and size
/// that are already there. A different copy of the same film has a different
/// codec.</item>
/// </list>
///
/// Bitrate is deliberately absent: it is size over duration, derived where it
/// is displayed, and stored nowhere so it cannot go stale against either.
/// </summary>
public sealed class V0012MovieMediaFacts : SqliteSqlMigration
{
    public override int Version => 12;

    public override string Name => "movie_media_facts";

    protected override string Sql =>
        """
        ALTER TABLE movie_entries ADD COLUMN runtime_minutes INTEGER NULL;
        ALTER TABLE movie_entries ADD COLUMN popularity REAL NULL;
        ALTER TABLE movie_entries ADD COLUMN vote_count INTEGER NULL;

        ALTER TABLE movie_wanted_state ADD COLUMN video_codec TEXT NULL;
        ALTER TABLE movie_wanted_state ADD COLUMN audio_codec TEXT NULL;
        ALTER TABLE movie_wanted_state ADD COLUMN audio_channels TEXT NULL;
        ALTER TABLE movie_wanted_state ADD COLUMN release_group TEXT NULL;

        CREATE INDEX IF NOT EXISTS ix_movie_entries_runtime_id
            ON movie_entries (COALESCE(runtime_minutes, -1), id);

        CREATE INDEX IF NOT EXISTS ix_movie_entries_popularity_id
            ON movie_entries (COALESCE(popularity, -1), id);
        """;
}
