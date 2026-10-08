using System.Collections.Concurrent;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.Metadata;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Realtime;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Realtime;

/// <summary>
/// #1179: every change to a film, a show or an episode announces itself, whoever made it.
/// The announcement comes from the database write, not from the screen or the handler that
/// asked for it, so background work (an import finishing, a list sync, a metadata or
/// catalogue job) is counted without each having to remember to publish.
/// </summary>
public sealed class TitleChangesAreAnnouncedTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-08T00:00:00Z");

    private sealed class Recorder : NullRealtimeEventPublisher, IRealtimeEventPublisher
    {
        public ConcurrentQueue<(string Type, string Id, string[] Libraries)> Titles { get; } = new();
        public ConcurrentQueue<(string Type, string Id)> Entities { get; } = new();

        public void Clear()
        {
            Titles.Clear();
            Entities.Clear();
        }

        public Task PublishTitleChangedAsync(string entityType, string entityId, IReadOnlyCollection<string> libraryIds, CancellationToken cancellationToken)
        {
            Titles.Enqueue((entityType, entityId, [.. libraryIds]));
            return Task.CompletedTask;
        }

        public override Task PublishEntityChangedAsync(string entityType, string entityId, CancellationToken cancellationToken)
        {
            Entities.Enqueue((entityType, entityId));
            return Task.CompletedTask;
        }
    }

    private static async Task<SqliteMovieCatalogRepository> MoviesAsync(TestStorage storage)
    {
        var clock = new FixedTimeProvider(Now);
        await new MoviesSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, clock), NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return new SqliteMovieCatalogRepository(storage.Factory, clock);
    }

    private static async Task<SqliteSeriesCatalogRepository> SeriesAsync(TestStorage storage)
    {
        var clock = new FixedTimeProvider(Now);
        await new SeriesSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, clock), NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return new SqliteSeriesCatalogRepository(storage.Factory, clock);
    }

    private static TitleChangeNotifier NotifierFor(TestStorage storage, Recorder recorder)
        => new(storage.Factory, recorder, NullLogger<TitleChangeNotifier>.Instance, TimeProvider.System);

    [Fact]
    public async Task An_intake_sync_adding_a_film_announces_it()
    {
        using var storage = TestStorage.Create();
        var movies = await MoviesAsync(storage);
        var recorder = new Recorder();
        var notifier = NotifierFor(storage, recorder);
        await notifier.FlushAsync(DelunoDatabaseNames.Movies, CancellationToken.None);
        recorder.Clear();

        // The same call IntakeSyncService makes for a list entry it has not seen.
        var movie = await movies.AddAsync(new CreateMovieRequest("Heat", 1995, "tt0113277"), CancellationToken.None);
        await notifier.FlushAsync(DelunoDatabaseNames.Movies, CancellationToken.None);

        Assert.Contains(recorder.Titles, title => title.Type == "Movie" && title.Id == movie.Id);
    }

    [Fact]
    public async Task A_background_import_announces_the_film_and_its_library()
    {
        using var storage = TestStorage.Create();
        var movies = await MoviesAsync(storage);
        var recorder = new Recorder();
        var notifier = NotifierFor(storage, recorder);
        await notifier.FlushAsync(DelunoDatabaseNames.Movies, CancellationToken.None);
        recorder.Clear();

        await movies.ImportExistingAsync(
            "lib-1", "Heat", 1995, "covered", "imported", "1080p", "1080p", true, "/media/Heat (1995).mkv", 1000, CancellationToken.None);
        await notifier.FlushAsync(DelunoDatabaseNames.Movies, CancellationToken.None);

        var announced = Assert.Single(recorder.Titles, title => title.Type == "Movie");
        Assert.Equal(["lib-1"], announced.Libraries);
    }

    [Fact]
    public async Task A_metadata_refresh_announces_the_film_it_changed_and_no_other()
    {
        using var storage = TestStorage.Create();
        var movies = await MoviesAsync(storage);
        var other = await movies.AddAsync(new CreateMovieRequest("Ronin", 1998, "tt0122690"), CancellationToken.None);
        var movie = await movies.AddAsync(new CreateMovieRequest("Heat", 1995, "tt0113277"), CancellationToken.None);
        var recorder = new Recorder();
        var notifier = NotifierFor(storage, recorder);
        await notifier.FlushAsync(DelunoDatabaseNames.Movies, CancellationToken.None);
        recorder.Clear();

        await movies.UpdateMetadataAsync(movie.Id, new MetadataSearchResult(
            Provider: "tmdb", ProviderId: "949", MediaType: "movies", Title: "Heat", OriginalTitle: "Heat", Year: 1995,
            Overview: "A crew and a detective.", PosterUrl: null, BackdropUrl: null, Rating: 8.2, Ratings: [], Genres: ["Crime"],
            ImdbId: "tt0113277", ExternalUrl: null), CancellationToken.None);
        await notifier.FlushAsync(DelunoDatabaseNames.Movies, CancellationToken.None);

        Assert.Contains(recorder.Titles, title => title.Id == movie.Id);
        Assert.DoesNotContain(recorder.Titles, title => title.Id == other.Id);
    }

    [Fact]
    public async Task A_catalogue_refresh_of_many_episodes_announces_one_show_event()
    {
        using var storage = TestStorage.Create();
        var series = await SeriesAsync(storage);
        var show = await series.AddAsync(new CreateSeriesRequest("The Wire", 2002, "tt0306414"), CancellationToken.None);
        var recorder = new Recorder();
        var notifier = NotifierFor(storage, recorder);
        await notifier.FlushAsync(DelunoDatabaseNames.Series, CancellationToken.None);
        recorder.Clear();

        // What a catalogue refresh does: write every episode of a show.
        await using (var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Series, CancellationToken.None))
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            for (var episode = 1; episode <= 1000; episode++)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    "INSERT INTO episode_entries (id, series_id, season_number, episode_number, monitored, created_utc, updated_utc) " +
                    "VALUES (@id, @series, 1, @episode, 1, '2026-10-08T00:00:00Z', '2026-10-08T00:00:00Z')";
                command.Parameters.Add(new SqliteParameter("@id", $"ep-{episode}"));
                command.Parameters.Add(new SqliteParameter("@series", show.Id));
                command.Parameters.Add(new SqliteParameter("@episode", episode));
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        await notifier.FlushAsync(DelunoDatabaseNames.Series, CancellationToken.None);

        Assert.Single(recorder.Titles, title => title.Type == "Series" && title.Id == show.Id);
        Assert.Single(recorder.Titles);
        // Too many episodes to name one by one: one note that episodes changed.
        Assert.Single(recorder.Entities, entity => entity is { Type: "Episode", Id: "*" });
        Assert.DoesNotContain(recorder.Entities, entity => entity.Type == "Episode" && entity.Id != "*");
    }

    [Fact]
    public async Task A_removed_film_says_that_films_changed_even_though_its_row_is_gone()
    {
        using var storage = TestStorage.Create();
        var movies = await MoviesAsync(storage);
        var movie = await movies.AddAsync(new CreateMovieRequest("Heat", 1995, "tt0113277"), CancellationToken.None);
        var recorder = new Recorder();
        var notifier = NotifierFor(storage, recorder);
        await notifier.FlushAsync(DelunoDatabaseNames.Movies, CancellationToken.None);
        recorder.Clear();

        await movies.DeleteAsync(movie.Id, CancellationToken.None);
        await notifier.FlushAsync(DelunoDatabaseNames.Movies, CancellationToken.None);

        Assert.Contains(recorder.Entities, entity => entity is { Type: "Movie", Id: "*" });
    }
}
