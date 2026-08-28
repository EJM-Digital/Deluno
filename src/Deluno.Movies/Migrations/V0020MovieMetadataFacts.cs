using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Movies.Migrations;

/// <summary>
/// Who made a film, and whether it is out yet — two answers the metadata
/// provider has always given and Deluno has never kept.
///
/// <para><b>Studio is the film's half of a question the shelf already asks of a
/// show.</b> A series has a <c>network</c> column (V0012) and a Network filter;
/// a film has neither, so the same question — <i>who made this</i> — could be
/// asked on one shelf and not the other. #306 counts Studio among the
/// twenty-seven fields Radarr can filter by and Deluno cannot.</para>
///
/// <para><b>Status is not the same word it is for a show.</b> TMDb answers
/// <c>Released</c>, <c>In Production</c>, <c>Post Production</c> or
/// <c>Planned</c> for a film, against <c>Returning Series</c> / <c>Ended</c> /
/// <c>Canceled</c> for a show. Same column name, same provider field, different
/// vocabulary — which is why it is a text column and not an enum, and why the
/// filter offers each kind its own options.</para>
///
/// <para><b>And it makes the shared write possible.</b> The metadata update
/// lives once in <c>SqliteMediaStateRepository</c> and runs against both
/// catalogues through <c>MediaTableMap</c>. Without these columns that one
/// statement could not name them, and the alternative was a second copy of the
/// write for movies — which is the shape that let <c>status</c> and
/// <c>network</c> go unwritten through four call sites in the first place.</para>
/// </summary>
public sealed class V0020MovieMetadataFacts : SqliteSqlMigration
{
    public override int Version => 20;

    public override string Name => "movie_metadata_facts";

    protected override string Sql =>
        """
        ALTER TABLE movie_entries ADD COLUMN status TEXT NULL;
        ALTER TABLE movie_entries ADD COLUMN studio TEXT NULL;

        CREATE INDEX IF NOT EXISTS ix_movie_entries_status_id
            ON movie_entries (COALESCE(status, ''), id);

        CREATE INDEX IF NOT EXISTS ix_movie_entries_studio_id
            ON movie_entries (lower(COALESCE(studio, '')), id);

        -- What is already known, from the blob it has been sitting in unread.
        -- A row written before the provider learnt to send these has no such
        -- key and stays NULL until a metadata refresh, which is the same
        -- bargain #326 makes about artwork.
        UPDATE movie_entries
        SET status = json_extract(metadata_json, '$.Status'),
            studio = json_extract(metadata_json, '$.Studio')
        WHERE metadata_json IS NOT NULL AND json_valid(metadata_json);
        """;
}
