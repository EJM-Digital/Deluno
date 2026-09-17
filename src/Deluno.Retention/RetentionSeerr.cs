using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Integrations.MediaServers;
using Deluno.Integrations.Seerr;
using Microsoft.Extensions.Logging;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Retention;

/// <summary>
/// Every request the connected Seerr servers hold, found by a title's TMDb or
/// TVDb id (#522). A season or episode is asked about through its show: Seerr
/// keeps one record per title.
/// </summary>
public sealed class RetentionSeerrIndex
{
    private readonly ILookup<string, SeerrRequest> _byTmdb;
    private readonly ILookup<string, SeerrRequest> _byTvdb;

    public RetentionSeerrIndex(IReadOnlyList<SeerrLibrary> libraries)
    {
        var requests = libraries.SelectMany(library => library.Requests).ToArray();
        _byTmdb = requests.Where(request => request.TmdbId is not null).ToLookup(request => $"{request.MediaType}:{request.TmdbId}", StringComparer.Ordinal);
        _byTvdb = requests.Where(request => request.TvdbId is not null && request.MediaType == "tv").ToLookup(request => request.TvdbId!, StringComparer.Ordinal);
        Users = libraries.SelectMany(library => library.Users).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyList<string> Users { get; }

    /// <returns>Null when the title carries no id Seerr could know it by.</returns>
    public IReadOnlyList<SeerrRequest>? Find(RetentionUnit unit)
    {
        var mediaType = RetentionLevels.MediaType(unit.Level);
        if (!string.IsNullOrWhiteSpace(unit.TmdbId))
        {
            var byTmdb = _byTmdb[$"{mediaType}:{unit.TmdbId}"].ToArray();
            if (byTmdb.Length > 0 || mediaType == "movies" || string.IsNullOrWhiteSpace(unit.TvdbId))
            {
                return byTmdb;
            }
        }

        return mediaType == "tv" && !string.IsNullOrWhiteSpace(unit.TvdbId) ? _byTvdb[unit.TvdbId].ToArray() : null;
    }
}

/// <summary>What retention needs from Seerr: its requests, and clearing a removed title.</summary>
public interface IRetentionSeerr
{
    /// <returns>The index, null when no Seerr server is connected, and why any server could not be read.</returns>
    Task<(RetentionSeerrIndex? Index, IReadOnlyList<string> Unreachable)> ReadAsync(CancellationToken cancellationToken);

    /// <returns>A sentence for the run, or null when there is no Seerr to tell.</returns>
    Task<string?> ClearAsync(string entityId, string mediaType, string title, string? tmdbId, string? tvdbId, CancellationToken cancellationToken);

    Task RetryAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<RetentionSeerrCleanup>> ListCleanupsAsync(CancellationToken cancellationToken);
}

/// <summary>A title a rule removed whose Seerr record could not be cleared yet.</summary>
public sealed record RetentionSeerrCleanup(
    string ServerId,
    string EntityId,
    string MediaType,
    string Title,
    string? TmdbId,
    string? TvdbId,
    int Attempts,
    string? Problem,
    DateTimeOffset CreatedUtc,
    DateTimeOffset AttemptedUtc);

/// <summary>
/// Clears a removed title from Seerr, so it shows as not available and its
/// requests are gone (#522). If Seerr cannot be reached the removal still
/// stands - the files and servers are Deluno's - and the clearing is tried
/// again at later checks and reported on the Seerr server until it succeeds.
/// </summary>
public sealed class RetentionSeerrService(
    ISeerrRepository servers,
    ISeerrClient client,
    Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider,
    ILogger<RetentionSeerrService> logger) : IRetentionSeerr
{
    /// <summary>Loads every enabled Seerr server's requests.</summary>
    /// <returns>The index, or why a server could not be read.</returns>
    public async Task<(RetentionSeerrIndex? Index, IReadOnlyList<string> Unreachable)> ReadAsync(CancellationToken cancellationToken)
    {
        var libraries = new List<SeerrLibrary>();
        var unreachable = new List<string>();
        foreach (var server in (await servers.ListAsync(cancellationToken)).Where(server => server.IsEnabled))
        {
            switch (await client.ListRequestsAsync(server, cancellationToken))
            {
                case MediaServerAnswer<SeerrLibrary>.Reported reported:
                    libraries.Add(reported.Value);
                    break;
                case MediaServerAnswer<SeerrLibrary>.Unreachable failed:
                    unreachable.Add(failed.Reason);
                    break;
                case MediaServerAnswer<SeerrLibrary>.NotSupported unsupported:
                    unreachable.Add(unsupported.Reason);
                    break;
            }
        }

        return (libraries.Count == 0 && unreachable.Count == 0 ? null : new RetentionSeerrIndex(libraries), unreachable);
    }

    /// <summary>Clears one removed title from every enabled Seerr server, recording what could not be.</summary>
    /// <returns>A sentence for the run, or null when there is no Seerr to tell.</returns>
    public async Task<string?> ClearAsync(string entityId, string mediaType, string title, string? tmdbId, string? tvdbId, CancellationToken cancellationToken)
    {
        var enabled = (await servers.ListAsync(cancellationToken)).Where(server => server.IsEnabled).ToArray();
        if (enabled.Length == 0)
        {
            return null;
        }

        var notes = new List<string>();
        foreach (var server in enabled)
        {
            var now = timeProvider.GetUtcNow();
            var problem = await TryClearAsync(server, mediaType, tmdbId, tvdbId, cancellationToken);
            if (problem is null)
            {
                await DeleteCleanupAsync(server.Id, entityId, cancellationToken);
                continue;
            }

            await SaveCleanupAsync(new RetentionSeerrCleanup(server.Id, entityId, mediaType, title, tmdbId, tvdbId, 1, problem, now, now), cancellationToken);
            notes.Add($"{server.Name} could not be cleared yet ({problem.TrimEnd('.')}); Deluno tries again at the next check.");
        }

        return notes.Count == 0 ? $"Cleared from {string.Join(", ", enabled.Select(server => server.Name))}." : string.Join(" ", notes);
    }

    /// <summary>Tries every clearing still owed again.</summary>
    public async Task RetryAsync(CancellationToken cancellationToken)
    {
        try
        {
            var byId = (await servers.ListAsync(cancellationToken)).ToDictionary(server => server.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var cleanup in await ListCleanupsAsync(cancellationToken))
            {
                if (!byId.TryGetValue(cleanup.ServerId, out var server))
                {
                    // The server is gone from Deluno; there is nothing left to tell.
                    await DeleteCleanupAsync(cleanup.ServerId, cleanup.EntityId, cancellationToken);
                    continue;
                }

                if (!server.IsEnabled)
                {
                    continue;
                }

                var problem = await TryClearAsync(server, cleanup.MediaType, cleanup.TmdbId, cleanup.TvdbId, cancellationToken);
                if (problem is null)
                {
                    await DeleteCleanupAsync(cleanup.ServerId, cleanup.EntityId, cancellationToken);
                }
                else
                {
                    await SaveCleanupAsync(cleanup with { Attempts = cleanup.Attempts + 1, Problem = problem, AttemptedUtc = timeProvider.GetUtcNow() }, cancellationToken);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Titles owed a Seerr clearing could not be tried again");
        }
    }

    private async Task<string?> TryClearAsync(SeerrConnection server, string mediaType, string? tmdbId, string? tvdbId, CancellationToken cancellationToken)
    {
        try
        {
            return await client.ClearAsync(server, mediaType, tmdbId, tvdbId, cancellationToken) switch
            {
                MediaServerAnswer<SeerrClearing>.Reported => null,
                MediaServerAnswer<SeerrClearing>.Unreachable failed => failed.Reason,
                MediaServerAnswer<SeerrClearing>.NotSupported unsupported => unsupported.Reason,
                _ => $"{server.Name} gave an answer Deluno did not expect."
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Clearing a title from {Server} failed", server.Name);
            return $"Deluno could not reach {server.Name}.";
        }
    }

    public async Task<IReadOnlyList<RetentionSeerrCleanup>> ListCleanupsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(Deluno.Infrastructure.Storage.DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT server_id, entity_id, media_type, title, tmdb_id, tvdb_id, attempts, problem, created_utc, attempted_utc FROM retention_seerr_cleanups ORDER BY created_utc;";
        var cleanups = new List<RetentionSeerrCleanup>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            cleanups.Add(new RetentionSeerrCleanup(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetString(7),
                DateTimeOffset.Parse(reader.GetString(8)), DateTimeOffset.Parse(reader.GetString(9))));
        }

        return cleanups;
    }

    private async Task SaveCleanupAsync(RetentionSeerrCleanup cleanup, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(Deluno.Infrastructure.Storage.DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO retention_seerr_cleanups (server_id, entity_id, media_type, title, tmdb_id, tvdb_id, attempts, problem, created_utc, attempted_utc)
            VALUES (@server, @entity, @mediaType, @title, @tmdb, @tvdb, @attempts, @problem, @created, @attempted)
            ON CONFLICT (server_id, entity_id) DO UPDATE SET
                attempts = excluded.attempts, problem = excluded.problem, attempted_utc = excluded.attempted_utc;
            """;
        AddParameter(command, "@server", cleanup.ServerId);
        AddParameter(command, "@entity", cleanup.EntityId);
        AddParameter(command, "@mediaType", cleanup.MediaType);
        AddParameter(command, "@title", cleanup.Title);
        AddParameter(command, "@tmdb", cleanup.TmdbId);
        AddParameter(command, "@tvdb", cleanup.TvdbId);
        AddParameter(command, "@attempts", cleanup.Attempts);
        AddParameter(command, "@problem", cleanup.Problem);
        AddParameter(command, "@created", cleanup.CreatedUtc.ToString("O"));
        AddParameter(command, "@attempted", cleanup.AttemptedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task DeleteCleanupAsync(string serverId, string entityId, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(Deluno.Infrastructure.Storage.DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM retention_seerr_cleanups WHERE server_id = @server AND entity_id = @entity;";
        AddParameter(command, "@server", serverId);
        AddParameter(command, "@entity", entityId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
