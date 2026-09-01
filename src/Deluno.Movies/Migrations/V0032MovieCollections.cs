using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Movies.Migrations;

/// <summary>
/// Stores a provider collection separately from its movie membership. A movie
/// can belong to more than one provider collection, so a join table is the
/// normalized "collection id on the title" rather than a lossy single column.
/// </summary>
public sealed class V0032MovieCollections : SqliteSqlMigration
{
    public override int Version => 32;

    public override string Name => "movie_collections";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS movie_collections (
            id TEXT PRIMARY KEY,
            provider TEXT NOT NULL,
            provider_id TEXT NOT NULL,
            library_id TEXT NOT NULL,
            library_name TEXT NOT NULL,
            root_path TEXT NOT NULL,
            name TEXT NOT NULL,
            overview TEXT NULL,
            poster_url TEXT NULL,
            backdrop_url TEXT NULL,
            monitored INTEGER NOT NULL DEFAULT 0,
            monitor_movies INTEGER NOT NULL DEFAULT 1,
            quality_profile_id TEXT NULL,
            quality_profile_name TEXT NULL,
            minimum_availability TEXT NOT NULL DEFAULT 'released',
            search_on_add INTEGER NOT NULL DEFAULT 0,
            last_synced_utc TEXT NULL,
            next_sync_utc TEXT NULL,
            last_sync_error TEXT NULL,
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ix_movie_collections_provider
            ON movie_collections (provider, provider_id, library_id);

        CREATE INDEX IF NOT EXISTS ix_movie_collections_due
            ON movie_collections (monitored, next_sync_utc);

        CREATE TABLE IF NOT EXISTS movie_collection_members (
            collection_id TEXT NOT NULL,
            provider_id TEXT NOT NULL,
            title TEXT NOT NULL,
            release_year INTEGER NULL,
            overview TEXT NULL,
            poster_url TEXT NULL,
            backdrop_url TEXT NULL,
            external_url TEXT NULL,
            imdb_id TEXT NULL,
            local_movie_id TEXT NULL,
            is_excluded INTEGER NOT NULL DEFAULT 0,
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL,
            PRIMARY KEY (collection_id, provider_id),
            FOREIGN KEY (collection_id) REFERENCES movie_collections(id) ON DELETE CASCADE,
            FOREIGN KEY (local_movie_id) REFERENCES movie_entries(id) ON DELETE SET NULL
        );

        CREATE INDEX IF NOT EXISTS ix_movie_collection_members_local_movie
            ON movie_collection_members (local_movie_id);

        CREATE INDEX IF NOT EXISTS ix_movie_collection_members_missing
            ON movie_collection_members (collection_id, local_movie_id, is_excluded);
        """;
}
