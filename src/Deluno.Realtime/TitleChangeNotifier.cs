using System.Globalization;
using System.Text;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Realtime;

/// <summary>
/// Announces every change to a film, a show or an episode, whoever made it (#1179).
///
/// <para>SQLite tells <see cref="TitleWriteSink"/> which rows a write touched, so an
/// import finishing, a list sync, a metadata or catalogue job, a subtitle scan, a
/// wanted-state change, a remove and an HTTP edit are all counted in this one place.
/// After a short quiet spell the rows are turned into titles and each title is
/// announced once: a 1,000-episode catalogue refresh is one show event, not 1,000.</para>
/// </summary>
public sealed class TitleChangeNotifier(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    IRealtimeEventPublisher publisher,
    ILogger<TitleChangeNotifier> logger,
    TimeProvider timeProvider,
    IRealtimeWatchers? watchers = null)
    : BackgroundService
{
    /// <summary>How long a burst of writes is gathered before it is announced. A coalescing window, not a rate limit.</summary>
    public static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(250);

    /// <summary>More titles than this in one burst are announced as "many things changed" plus a note to each library and each open page.</summary>
    public const int MaxTitlesAnnouncedOneByOne = 200;

    /// <summary>More episodes than this in one burst are covered by their show's event alone.</summary>
    public const int MaxEpisodesAnnouncedOneByOne = 25;

    private const int ChunkSize = 400;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.WhenAll(
            RunAsync(DelunoDatabaseNames.Movies, stoppingToken),
            RunAsync(DelunoDatabaseNames.Series, stoppingToken));

    private async Task RunAsync(string databaseName, CancellationToken stoppingToken)
    {
        var sink = SinkFor(databaseName);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await sink.Signal.WaitAsync(stoppingToken);
                await Task.Delay(CoalesceWindow, timeProvider, stoppingToken);
                try
                {
                    await FlushAsync(databaseName, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogDebug(exception, "Changes to {Database} could not be announced.", databaseName);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private TitleWriteSink SinkFor(string databaseName)
        => TitleWriteTables.SinkFor(databaseConnectionFactory.GetDatabasePath(databaseName), databaseName)!;

    /// <summary>Announces what has been written to the movies or series database since the last time. Exposed for tests.</summary>
    public async Task FlushAsync(string databaseName, CancellationToken cancellationToken)
    {
        var batch = SinkFor(databaseName).Take();
        if (batch.IsEmpty)
        {
            return;
        }

        var isMovies = string.Equals(databaseName, DelunoDatabaseNames.Movies, StringComparison.OrdinalIgnoreCase);
        var resolved = await ResolveAsync(databaseName, isMovies, batch, cancellationToken);
        var entityType = isMovies ? "Movie" : "Series";

        if (resolved.Titles.Count <= MaxTitlesAnnouncedOneByOne && !batch.Overflow)
        {
            foreach (var (id, libraries) in resolved.Titles)
            {
                await publisher.PublishTitleChangedAsync(entityType, id, libraries, cancellationToken);
            }

            if (resolved.Episodes.Count is > 0 and <= MaxEpisodesAnnouncedOneByOne)
            {
                foreach (var episodeId in resolved.Episodes)
                {
                    await publisher.PublishEntityChangedAsync("Episode", episodeId, cancellationToken);
                }
            }
            else if (resolved.Episodes.Count > 0)
            {
                await publisher.PublishEntityChangedAsync("Episode", "*", cancellationToken);
            }

            if (resolved.SomethingGone)
            {
                await publisher.PublishEntityChangedAsync(entityType, "*", cancellationToken);
            }

            return;
        }

        // A bulk write: tell every library and the Dashboard once, and tell the pages
        // that are actually open, rather than flooding every title's own subject.
        await publisher.PublishEntityChangedAsync(entityType, "*", cancellationToken);
        if (!isMovies)
        {
            await publisher.PublishEntityChangedAsync("Episode", "*", cancellationToken);
        }

        foreach (var libraryId in resolved.Titles.Values.SelectMany(libraries => libraries).Distinct(StringComparer.Ordinal))
        {
            await publisher.PublishEntityChangedAsync("Library", libraryId, cancellationToken);
        }

        if (watchers is not null)
        {
            foreach (var (id, libraries) in resolved.Titles)
            {
                if (watchers.Count(RealtimeGroups.Title(id)) > 0)
                {
                    await publisher.PublishTitleChangedAsync(entityType, id, libraries, cancellationToken);
                }
            }
        }
    }

    private sealed record Resolved(
        Dictionary<string, HashSet<string>> Titles,
        HashSet<string> Episodes,
        bool SomethingGone);

    private async Task<Resolved> ResolveAsync(
        string databaseName,
        bool isMovies,
        TitleWriteBatch batch,
        CancellationToken cancellationToken)
    {
        var titles = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var episodes = new HashSet<string>(StringComparer.Ordinal);
        var gone = false;

        void Title(string id, string? libraryId)
        {
            if (!titles.TryGetValue(id, out var libraries))
            {
                libraries = new HashSet<string>(StringComparer.Ordinal);
                titles[id] = libraries;
            }

            if (!string.IsNullOrEmpty(libraryId))
            {
                libraries.Add(libraryId);
            }
        }

        await using var connection = await databaseConnectionFactory.OpenReadOnlyConnectionAsync(databaseName, cancellationToken);

        // Each row is read as (title id, library id, episode id): a table that has no such column returns NULL for it.
        var reads = isMovies
            ? new Dictionary<string, string>
            {
                ["movie_entries"] = "SELECT id, NULL, NULL FROM movie_entries WHERE rowid IN ({0})",
                ["movie_wanted_state"] = "SELECT movie_id, library_id, NULL FROM movie_wanted_state WHERE rowid IN ({0})",
                ["movie_subtitle_state"] = "SELECT movie_id, NULL, NULL FROM movie_subtitle_state WHERE rowid IN ({0})"
            }
            : new Dictionary<string, string>
            {
                ["series_entries"] = "SELECT id, NULL, NULL FROM series_entries WHERE rowid IN ({0})",
                ["series_wanted_state"] = "SELECT series_id, library_id, NULL FROM series_wanted_state WHERE rowid IN ({0})",
                ["season_entries"] = "SELECT series_id, NULL, NULL FROM season_entries WHERE rowid IN ({0})",
                ["episode_entries"] = "SELECT series_id, NULL, id FROM episode_entries WHERE rowid IN ({0})",
                ["episode_wanted_state"] = "SELECT series_id, library_id, episode_id FROM episode_wanted_state WHERE rowid IN ({0})",
                ["episode_subtitle_state"] =
                    "SELECT e.series_id, NULL, e.id FROM episode_subtitle_state s JOIN episode_entries e ON e.id = s.episode_id WHERE s.rowid IN ({0})"
            };

        foreach (var (table, rowIds) in batch.Rows)
        {
            if (!reads.TryGetValue(table, out var template))
            {
                // A collection changed: films changed, but which is not said.
                gone |= TitleWriteTables.Unattributed.Contains(table);
                continue;
            }

            var found = 0;
            foreach (var chunk in rowIds.Chunk(ChunkSize))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = string.Format(
                    CultureInfo.InvariantCulture,
                    template,
                    string.Join(',', chunk.Select(id => id.ToString(CultureInfo.InvariantCulture))));
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    found++;
                    Title(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
                    if (!reader.IsDBNull(2))
                    {
                        episodes.Add(reader.GetString(2));
                    }
                }
            }

            // A row that is no longer there was removed (or its parent was): the title is unknown, so say "many".
            if (found < rowIds.Count)
            {
                gone = true;
            }
        }

        // Every library a changed title is in, so each library's grid hears of it.
        var missingLibraries = titles.Where(pair => pair.Value.Count == 0).Select(pair => pair.Key).ToArray();
        if (missingLibraries.Length > 0)
        {
            var sql = isMovies
                ? "SELECT movie_id, library_id FROM movie_wanted_state WHERE movie_id IN ({0})"
                : "SELECT series_id, library_id FROM series_wanted_state WHERE series_id IN ({0})";
            foreach (var chunk in missingLibraries.Chunk(ChunkSize))
            {
                await using var command = connection.CreateCommand();
                var names = new StringBuilder();
                for (var i = 0; i < chunk.Length; i++)
                {
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = $"@p{i}";
                    parameter.Value = chunk[i];
                    command.Parameters.Add(parameter);
                    names.Append(i == 0 ? "" : ",").Append(parameter.ParameterName);
                }

                command.CommandText = string.Format(CultureInfo.InvariantCulture, sql, names);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    Title(reader.GetString(0), reader.GetString(1));
                }
            }
        }

        return new Resolved(titles, episodes, gone);
    }
}
