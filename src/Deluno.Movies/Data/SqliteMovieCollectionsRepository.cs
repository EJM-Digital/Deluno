using System.Data.Common;
using System.Globalization;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Integrations.Metadata;
using Deluno.Movies.Contracts;
using Microsoft.Data.Sqlite;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Movies.Data;

public sealed class SqliteMovieCollectionsRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider,
    IUnifiedExclusionRepository? unifiedExclusionRepository = null) : IMovieCollectionsRepository
{
    private const string DatabaseName = DelunoDatabaseNames.Movies;

    public async Task<IReadOnlyList<MovieCollectionItem>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DatabaseName, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = CollectionSelectSql + " GROUP BY c.id ORDER BY c.name COLLATE NOCASE ASC, c.id ASC;";

        var items = new List<MovieCollectionItem>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadCollection(reader));
        }

        return items;
    }

    public async Task<MovieCollectionItem?> GetAsync(string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DatabaseName, cancellationToken);
        return await ReadByIdAsync(connection, id.Trim(), cancellationToken);
    }

    public async Task<MovieCollectionItem> UpsertAsync(
        string libraryId,
        string libraryName,
        string rootPath,
        string? qualityProfileId,
        string? qualityProfileName,
        CreateMovieCollectionRequest request,
        MetadataCollection metadata,
        CancellationToken cancellationToken)
    {
        var normalizedLibraryId = Required(libraryId, nameof(libraryId));
        var normalizedProvider = Required(metadata.Provider, nameof(metadata.Provider)).ToLowerInvariant();
        var normalizedProviderId = Required(metadata.ProviderId, nameof(metadata.ProviderId));
        var now = timeProvider.GetUtcNow().ToString("O");
        var id = Guid.CreateVersion7().ToString("N");
        var minimumAvailability = MovieAvailability.Normalize(request.MinimumAvailability);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DatabaseName, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO movie_collections (
                    id, provider, provider_id, library_id, library_name, root_path,
                    name, overview, poster_url, backdrop_url, monitored, monitor_movies,
                    quality_profile_id, quality_profile_name, minimum_availability,
                    search_on_add, last_synced_utc, next_sync_utc, last_sync_error,
                    created_utc, updated_utc
                )
                VALUES (
                    @id, @provider, @providerId, @libraryId, @libraryName, @rootPath,
                    @name, @overview, @posterUrl, @backdropUrl, @monitored, @monitorMovies,
                    @qualityProfileId, @qualityProfileName, @minimumAvailability,
                    @searchOnAdd, NULL, NULL, NULL, @now, @now
                )
                ON CONFLICT(provider, provider_id, library_id) DO UPDATE SET
                    library_name = excluded.library_name,
                    root_path = excluded.root_path,
                    name = excluded.name,
                    overview = excluded.overview,
                    poster_url = excluded.poster_url,
                    backdrop_url = excluded.backdrop_url,
                    monitored = excluded.monitored,
                    monitor_movies = excluded.monitor_movies,
                    quality_profile_id = excluded.quality_profile_id,
                    quality_profile_name = excluded.quality_profile_name,
                    minimum_availability = excluded.minimum_availability,
                    search_on_add = excluded.search_on_add,
                    updated_utc = excluded.updated_utc;
                """;
            AddParameter(command, "@id", id);
            AddParameter(command, "@provider", normalizedProvider);
            AddParameter(command, "@providerId", normalizedProviderId);
            AddParameter(command, "@libraryId", normalizedLibraryId);
            AddParameter(command, "@libraryName", Required(libraryName, nameof(libraryName)));
            AddParameter(command, "@rootPath", Required(rootPath, nameof(rootPath)));
            AddParameter(command, "@name", Required(metadata.Name, nameof(metadata.Name)));
            AddParameter(command, "@overview", Normalize(metadata.Overview));
            AddParameter(command, "@posterUrl", Normalize(metadata.PosterUrl));
            AddParameter(command, "@backdropUrl", Normalize(metadata.BackdropUrl));
            AddParameter(command, "@monitored", request.Monitored ? 1 : 0);
            AddParameter(command, "@monitorMovies", request.MonitorMovies ? 1 : 0);
            AddParameter(command, "@qualityProfileId", Normalize(request.QualityProfileId ?? qualityProfileId));
            AddParameter(command, "@qualityProfileName", Normalize(qualityProfileName));
            AddParameter(command, "@minimumAvailability", minimumAvailability);
            AddParameter(command, "@searchOnAdd", request.SearchOnAdd ? 1 : 0);
            AddParameter(command, "@now", now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return await ReadByProviderAsync(connection, normalizedProvider, normalizedProviderId, normalizedLibraryId, cancellationToken)
            ?? throw new InvalidOperationException("The movie collection could not be read after insertion.");
    }

    public async Task<IReadOnlyList<MovieCollectionItem>> ClaimDueAsync(
        DateTimeOffset now,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        var next = now.Add(interval).ToString("O");
        var nowText = now.ToString("O");
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DatabaseName, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var items = new List<MovieCollectionItem>();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = CollectionSelectSql + " WHERE c.monitored = 1 AND (c.next_sync_utc IS NULL OR c.next_sync_utc <= @now) GROUP BY c.id ORDER BY c.next_sync_utc ASC, c.name COLLATE NOCASE ASC;";
            AddParameter(command, "@now", nowText);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(ReadCollection(reader));
            }
        }

        var claimed = new List<MovieCollectionItem>(items.Count);
        foreach (var item in items)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE movie_collections SET next_sync_utc = @next, updated_utc = @now WHERE id = @id AND monitored = 1 AND (next_sync_utc IS NULL OR next_sync_utc <= @now);";
            AddParameter(update, "@next", next);
            AddParameter(update, "@now", nowText);
            AddParameter(update, "@id", item.Id);
            if (await update.ExecuteNonQueryAsync(cancellationToken) > 0)
            {
                claimed.Add(item);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return claimed.Select(item => item with { NextSyncUtc = DateTimeOffset.Parse(next, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) }).ToArray();
    }

    public async Task<IReadOnlyList<MovieCollectionMemberItem>> ListMembersAsync(
        string collectionId,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DatabaseName, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT collection_id, provider_id, title, release_year, overview, poster_url,
                   backdrop_url, external_url, imdb_id, local_movie_id, is_excluded, updated_utc
            FROM movie_collection_members
            WHERE collection_id = @collectionId
            ORDER BY COALESCE(release_year, 9999) ASC, title COLLATE NOCASE ASC, provider_id ASC;
            """;
        AddParameter(command, "@collectionId", collectionId);

        var items = new List<MovieCollectionMemberItem>();
        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(ReadMember(reader));
            }
        }

        if (unifiedExclusionRepository is null)
        {
            return items;
        }

        var collection = await ReadByIdAsync(connection, collectionId, cancellationToken);
        if (collection is not null && items.Any(item => item.IsExcluded))
        {
            foreach (var member in items.Where(item => item.IsExcluded))
            {
                await unifiedExclusionRepository.UpsertAsync(
                    new UpsertMediaExclusionRequest(
                        MediaType: "movies",
                        SourceKind: MediaExclusionSourceKinds.Collection,
                        SourceId: collection.Id,
                        SourceName: collection.Name,
                        Provider: collection.Provider,
                        EntryKey: member.ProviderId,
                        Title: member.Title,
                        Year: member.ReleaseYear,
                        ImdbId: member.ImdbId,
                        DurationDays: null,
                        Reason: "Excluded from collection by user"),
                    cancellationToken);
            }

            // The legacy bit was only a compatibility bridge. Clear it after
            // the shared record exists so deleting the shared decision really
            // makes this member eligible again.
            using var clearLegacy = connection.CreateCommand();
            clearLegacy.CommandText = "UPDATE movie_collection_members SET is_excluded = 0 WHERE collection_id = @collectionId AND is_excluded = 1;";
            AddParameter(clearLegacy, "@collectionId", collectionId);
            await clearLegacy.ExecuteNonQueryAsync(cancellationToken);
        }

        var exclusions = await unifiedExclusionRepository.ListActiveAsync(
            mediaType: "movies",
            sourceKind: MediaExclusionSourceKinds.Collection,
            sourceId: collectionId,
            cancellationToken);
        var excludedKeys = exclusions
            .Select(item => item.EntryKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return items
            .Select(item => item with { IsExcluded = excludedKeys.Contains(item.ProviderId) })
            .ToArray();
    }

    public async Task SaveSnapshotAsync(
        string collectionId,
        MetadataCollection metadata,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DatabaseName, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var nowText = now.ToString("O");

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE movie_collections
                SET name = @name,
                    overview = @overview,
                    poster_url = @posterUrl,
                    backdrop_url = @backdropUrl,
                    last_sync_error = NULL,
                    last_synced_utc = @now,
                    updated_utc = @now
                WHERE id = @id;
                """;
            AddParameter(update, "@name", Required(metadata.Name, nameof(metadata.Name)));
            AddParameter(update, "@overview", Normalize(metadata.Overview));
            AddParameter(update, "@posterUrl", Normalize(metadata.PosterUrl));
            AddParameter(update, "@backdropUrl", Normalize(metadata.BackdropUrl));
            AddParameter(update, "@now", nowText);
            AddParameter(update, "@id", collectionId);
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                throw new InvalidOperationException("Movie collection not found.");
            }
        }

        var providerIds = metadata.Movies
            .Where(movie => !string.IsNullOrWhiteSpace(movie.ProviderId) && !string.IsNullOrWhiteSpace(movie.Title))
            .Select(movie => movie.ProviderId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (providerIds.Length == 0)
        {
            using var deleteAll = connection.CreateCommand();
            deleteAll.Transaction = transaction;
            deleteAll.CommandText = "DELETE FROM movie_collection_members WHERE collection_id = @collectionId;";
            AddParameter(deleteAll, "@collectionId", collectionId);
            await deleteAll.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            using var deleteStale = connection.CreateCommand();
            deleteStale.Transaction = transaction;
            var parameters = providerIds.Select((_, index) => $"@providerId{index}").ToArray();
            deleteStale.CommandText = $"DELETE FROM movie_collection_members WHERE collection_id = @collectionId AND provider_id NOT IN ({string.Join(", ", parameters)});";
            AddParameter(deleteStale, "@collectionId", collectionId);
            for (var index = 0; index < providerIds.Length; index++) AddParameter(deleteStale, parameters[index], providerIds[index]);
            await deleteStale.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var movie in metadata.Movies.Where(movie => !string.IsNullOrWhiteSpace(movie.ProviderId) && !string.IsNullOrWhiteSpace(movie.Title)))
        {
            using var upsert = connection.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText =
                """
                INSERT INTO movie_collection_members (
                    collection_id, provider_id, title, release_year, overview, poster_url,
                    backdrop_url, external_url, imdb_id, local_movie_id, is_excluded,
                    created_utc, updated_utc
                )
                VALUES (
                    @collectionId, @providerId, @title, @releaseYear, @overview, @posterUrl,
                    @backdropUrl, @externalUrl, @imdbId, NULL, 0, @now, @now
                )
                ON CONFLICT(collection_id, provider_id) DO UPDATE SET
                    title = excluded.title,
                    release_year = excluded.release_year,
                    overview = excluded.overview,
                    poster_url = excluded.poster_url,
                    backdrop_url = excluded.backdrop_url,
                    external_url = excluded.external_url,
                    imdb_id = excluded.imdb_id,
                    updated_utc = excluded.updated_utc;
                """;
            AddParameter(upsert, "@collectionId", collectionId);
            AddParameter(upsert, "@providerId", movie.ProviderId.Trim());
            AddParameter(upsert, "@title", movie.Title.Trim());
            AddParameter(upsert, "@releaseYear", movie.Year);
            AddParameter(upsert, "@overview", Normalize(movie.Overview));
            AddParameter(upsert, "@posterUrl", Normalize(movie.PosterUrl));
            AddParameter(upsert, "@backdropUrl", Normalize(movie.BackdropUrl));
            AddParameter(upsert, "@externalUrl", Normalize(movie.ExternalUrl));
            AddParameter(upsert, "@imdbId", Normalize(movie.ImdbId));
            AddParameter(upsert, "@now", nowText);
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var relink = connection.CreateCommand())
        {
            relink.Transaction = transaction;
            relink.CommandText =
                """
                UPDATE movie_collection_members
                SET local_movie_id = (
                    SELECT id
                    FROM movie_entries
                    WHERE lower(metadata_provider) = 'tmdb'
                      AND metadata_provider_id = movie_collection_members.provider_id
                    ORDER BY created_utc ASC
                    LIMIT 1
                ),
                    updated_utc = @now
                WHERE collection_id = @collectionId;
                """;
            AddParameter(relink, "@now", nowText);
            AddParameter(relink, "@collectionId", collectionId);
            await relink.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<MovieCollectionItem?> UpdateAsync(
        string id,
        UpdateMovieCollectionRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DatabaseName, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE movie_collections
            SET monitored = COALESCE(@monitored, monitored),
                monitor_movies = COALESCE(@monitorMovies, monitor_movies),
                quality_profile_id = COALESCE(@qualityProfileId, quality_profile_id),
                minimum_availability = COALESCE(@minimumAvailability, minimum_availability),
                search_on_add = COALESCE(@searchOnAdd, search_on_add),
                updated_utc = @now
            WHERE id = @id;
            """;
        AddParameter(command, "@monitored", request.Monitored.HasValue ? request.Monitored.Value ? 1 : 0 : null);
        AddParameter(command, "@monitorMovies", request.MonitorMovies.HasValue ? request.MonitorMovies.Value ? 1 : 0 : null);
        AddParameter(command, "@qualityProfileId", Normalize(request.QualityProfileId));
        AddParameter(command, "@minimumAvailability", string.IsNullOrWhiteSpace(request.MinimumAvailability) ? null : MovieAvailability.Normalize(request.MinimumAvailability));
        AddParameter(command, "@searchOnAdd", request.SearchOnAdd.HasValue ? request.SearchOnAdd.Value ? 1 : 0 : null);
        AddParameter(command, "@now", timeProvider.GetUtcNow().ToString("O"));
        AddParameter(command, "@id", id);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            return null;
        }

        return await ReadByIdAsync(connection, id, cancellationToken);
    }

    public async Task<bool> LinkMovieAsync(
        string collectionId,
        string providerId,
        string movieId,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DatabaseName, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE movie_collection_members SET local_movie_id = @movieId, updated_utc = @now WHERE collection_id = @collectionId AND provider_id = @providerId;";
        AddParameter(command, "@movieId", movieId);
        AddParameter(command, "@collectionId", collectionId);
        AddParameter(command, "@providerId", providerId);
        AddParameter(command, "@now", timeProvider.GetUtcNow().ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> SetMemberExcludedAsync(
        string collectionId,
        string providerId,
        bool excluded,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DatabaseName, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE movie_collection_members SET is_excluded = @excluded, updated_utc = @now WHERE collection_id = @collectionId AND provider_id = @providerId;";
        AddParameter(command, "@excluded", unifiedExclusionRepository is null && excluded ? 1 : 0);
        AddParameter(command, "@collectionId", collectionId);
        AddParameter(command, "@providerId", providerId);
        AddParameter(command, "@now", timeProvider.GetUtcNow().ToString("O"));
        var updated = await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        if (!updated || unifiedExclusionRepository is null)
        {
            return updated;
        }

        var collection = await ReadByIdAsync(connection, collectionId, cancellationToken);
        var member = await ReadMemberAsync(connection, collectionId, providerId, cancellationToken);
        if (collection is null || member is null)
        {
            return false;
        }

        if (excluded)
        {
            await unifiedExclusionRepository.UpsertAsync(
                new UpsertMediaExclusionRequest(
                    MediaType: "movies",
                    SourceKind: MediaExclusionSourceKinds.Collection,
                    SourceId: collection.Id,
                    SourceName: collection.Name,
                    Provider: collection.Provider,
                    EntryKey: member.ProviderId,
                    Title: member.Title,
                    Year: member.ReleaseYear,
                    ImdbId: member.ImdbId,
                    DurationDays: null,
                    Reason: "Excluded from collection by user"),
                cancellationToken);
        }
        else
        {
            await unifiedExclusionRepository.DeleteByScopeAsync(
                MediaExclusionSourceKinds.Collection,
                collectionId,
                providerId,
                cancellationToken);
        }

        return true;
    }

    public async Task RecordSyncResultAsync(
        string collectionId,
        DateTimeOffset nextSyncUtc,
        DateTimeOffset? lastSyncedUtc,
        string? error,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DatabaseName, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE movie_collections
            SET next_sync_utc = @nextSyncUtc,
                last_synced_utc = COALESCE(@lastSyncedUtc, last_synced_utc),
                last_sync_error = @error,
                updated_utc = @now
            WHERE id = @id;
            """;
        AddParameter(command, "@nextSyncUtc", nextSyncUtc.ToString("O"));
        AddParameter(command, "@lastSyncedUtc", lastSyncedUtc?.ToString("O"));
        AddParameter(command, "@error", Normalize(error));
        AddParameter(command, "@now", timeProvider.GetUtcNow().ToString("O"));
        AddParameter(command, "@id", collectionId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string CollectionSelectSql =
        """
        SELECT
            c.id, c.provider, c.provider_id, c.name, c.overview, c.poster_url, c.backdrop_url,
            c.library_id, c.library_name, c.root_path, c.monitored, c.monitor_movies,
            c.quality_profile_id, c.quality_profile_name, c.minimum_availability, c.search_on_add,
            COUNT(m.provider_id) AS member_count,
            COALESCE(SUM(CASE WHEN m.local_movie_id IS NOT NULL THEN 1 ELSE 0 END), 0) AS held_count,
            c.last_synced_utc, c.next_sync_utc, c.last_sync_error,
            c.created_utc, c.updated_utc
        FROM movie_collections c
        LEFT JOIN movie_collection_members m ON m.collection_id = c.id
        """;

    private static async Task<MovieCollectionItem?> ReadByIdAsync(
        DbConnection connection,
        string id,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = CollectionSelectSql + " WHERE c.id = @id GROUP BY c.id LIMIT 1;";
        AddParameter(command, "@id", id);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCollection(reader) : null;
    }

    private static async Task<MovieCollectionItem?> ReadByProviderAsync(
        DbConnection connection,
        string provider,
        string providerId,
        string libraryId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = CollectionSelectSql + " WHERE c.provider = @provider AND c.provider_id = @providerId AND c.library_id = @libraryId GROUP BY c.id LIMIT 1;";
        AddParameter(command, "@provider", provider);
        AddParameter(command, "@providerId", providerId);
        AddParameter(command, "@libraryId", libraryId);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCollection(reader) : null;
    }

    private static MovieCollectionItem ReadCollection(DbDataReader reader)
    {
        var memberCount = reader.GetInt32(16);
        var heldCount = reader.GetInt32(17);
        return new MovieCollectionItem(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            ReadNullableString(reader, 4),
            ReadNullableString(reader, 5),
            ReadNullableString(reader, 6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetInt64(10) == 1,
            reader.GetInt64(11) == 1,
            ReadNullableString(reader, 12),
            ReadNullableString(reader, 13),
            MovieAvailability.Normalize(reader.IsDBNull(14) ? null : reader.GetString(14)),
            reader.GetInt64(15) == 1,
            memberCount,
            heldCount,
            Math.Max(0, memberCount - heldCount),
            ReadNullableTimestamp(reader, 18),
            ReadNullableTimestamp(reader, 19),
            ReadNullableString(reader, 20),
            ParseTimestamp(reader.GetString(21)),
            ParseTimestamp(reader.GetString(22)));
    }

    private static async Task<MovieCollectionMemberItem?> ReadMemberAsync(
        DbConnection connection,
        string collectionId,
        string providerId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT collection_id, provider_id, title, release_year, overview, poster_url,
                   backdrop_url, external_url, imdb_id, local_movie_id, is_excluded, updated_utc
            FROM movie_collection_members
            WHERE collection_id = @collectionId AND provider_id = @providerId
            LIMIT 1;
            """;
        AddParameter(command, "@collectionId", collectionId);
        AddParameter(command, "@providerId", providerId);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadMember(reader) : null;
    }

    private static MovieCollectionMemberItem ReadMember(DbDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3),
            ReadNullableString(reader, 4),
            ReadNullableString(reader, 5),
            ReadNullableString(reader, 6),
            ReadNullableString(reader, 7),
            ReadNullableString(reader, 8),
            ReadNullableString(reader, 9),
            reader.GetInt64(10) == 1,
            ParseTimestamp(reader.GetString(11)));

    private static string Required(string? value, string parameterName)
        => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A value is required.", parameterName) : value.Trim();

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ReadNullableString(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? ReadNullableTimestamp(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : ParseTimestamp(reader.GetString(ordinal));

    private static DateTimeOffset ParseTimestamp(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
