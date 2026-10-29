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
    public async Task Twenty_five_changed_episodes_are_each_announced_by_their_own_id_and_one_more_makes_it_one_note()
    {
        using var storage = TestStorage.Create();
        var series = await SeriesAsync(storage);
        var show = await series.AddAsync(new CreateSeriesRequest("The Wire", 2002, "tt0306414"), CancellationToken.None);
        var recorder = new Recorder();
        var notifier = NotifierFor(storage, recorder);
        await notifier.FlushAsync(DelunoDatabaseNames.Series, CancellationToken.None);

        async Task WriteEpisodesAsync(int from, int to)
        {
            recorder.Clear();
            await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Series, CancellationToken.None);
            for (var episode = from; episode <= to; episode++)
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    "INSERT INTO episode_entries (id, series_id, season_number, episode_number, monitored, created_utc, updated_utc) " +
                    "VALUES (@id, @series, 1, @episode, 1, '2026-10-08T00:00:00Z', '2026-10-08T00:00:00Z')";
                command.Parameters.Add(new SqliteParameter("@id", $"ep-{episode}"));
                command.Parameters.Add(new SqliteParameter("@series", show.Id));
                command.Parameters.Add(new SqliteParameter("@episode", episode));
                await command.ExecuteNonQueryAsync();
            }

            await notifier.FlushAsync(DelunoDatabaseNames.Series, CancellationToken.None);
        }

        await WriteEpisodesAsync(1, 25);
        var named = recorder.Entities.Where(entity => entity.Type == "Episode").Select(entity => entity.Id).Order().ToArray();
        Assert.Equal(Enumerable.Range(1, 25).Select(episode => $"ep-{episode}").Order().ToArray(), named);

        await WriteEpisodesAsync(26, 51);
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

    private sealed class Watching(params string[] subjects) : IRealtimeWatchers
    {
        public int Count(string subject) => subjects.Contains(subject) ? 1 : 0;
    }

    [Fact]
    public async Task A_burst_of_more_than_200_titles_is_one_many_changed_note_a_note_to_each_library_and_only_the_title_pages_that_are_open()
    {
        using var storage = TestStorage.Create();
        var movies = await MoviesAsync(storage);
        var recorder = new Recorder();
        var ids = new List<string>();
        for (var index = 0; index < TitleChangeNotifier.MaxTitlesAnnouncedOneByOne + 1; index++)
        {
            var movie = await movies.AddAsync(new CreateMovieRequest($"Film {index}", 1990 + index % 30, null), CancellationToken.None);
            await movies.EnsureWantedStateAsync(movie.Id, "lib-x", "missing", "Wanted", hasFile: false, null, "WEB 1080p", false, CancellationToken.None);
            ids.Add(movie.Id);
        }

        var notifier = new TitleChangeNotifier(storage.Factory, recorder, NullLogger<TitleChangeNotifier>.Instance, TimeProvider.System, new Watching($"title:{ids[7]}"));

        await notifier.FlushAsync(DelunoDatabaseNames.Movies, CancellationToken.None);

        Assert.Contains(recorder.Entities, entity => entity is { Type: "Movie", Id: "*" });
        Assert.Contains(recorder.Entities, entity => entity is { Type: "Library", Id: "lib-x" });
        var open = Assert.Single(recorder.Titles);
        Assert.Equal(ids[7], open.Id);
        Assert.Equal(["lib-x"], open.Libraries);
    }

    [Fact]
    public async Task Wanted_state_and_subtitle_state_writes_are_attributed_to_the_right_title_library_and_episode()
    {
        using var storage = TestStorage.Create();
        var movies = await MoviesAsync(storage);
        var series = await SeriesAsync(storage);
        var wantedFilm = await movies.AddAsync(new CreateMovieRequest("Heat", 1995, null), CancellationToken.None);
        var subtitledFilm = await movies.AddAsync(new CreateMovieRequest("Ronin", 1998, null), CancellationToken.None);
        var show = await series.AddAsync(new CreateSeriesRequest("The Wire", 2002, null), CancellationToken.None);
        var recorder = new Recorder();
        var notifier = NotifierFor(storage, recorder);
        await notifier.FlushAsync(DelunoDatabaseNames.Movies, CancellationToken.None);
        await notifier.FlushAsync(DelunoDatabaseNames.Series, CancellationToken.None);
        recorder.Clear();

        await movies.EnsureWantedStateAsync(wantedFilm.Id, "lib-1", "missing", "Wanted", hasFile: false, null, "WEB 1080p", false, CancellationToken.None);
        await ExecuteAsync(storage, DelunoDatabaseNames.Movies,
            $"INSERT INTO movie_subtitle_state (movie_id, language, source, created_utc, updated_utc) VALUES ('{subtitledFilm.Id}', 'en', 'sidecar', '2026-10-08T00:00:00Z', '2026-10-08T00:00:00Z')");
        await notifier.FlushAsync(DelunoDatabaseNames.Movies, CancellationToken.None);

        var wanted = Assert.Single(recorder.Titles, title => title.Id == wantedFilm.Id);
        Assert.Equal(["lib-1"], wanted.Libraries);
        Assert.Single(recorder.Titles, title => title.Id == subtitledFilm.Id);
        Assert.Equal(2, recorder.Titles.Count);
        recorder.Clear();

        await ExecuteAsync(storage, DelunoDatabaseNames.Series,
            $"INSERT INTO series_wanted_state (series_id, library_id, wanted_status, wanted_reason, updated_utc) VALUES ('{show.Id}', 'lib-tv', 'missing', 'Wanted', '2026-10-08T00:00:00Z')");
        await ExecuteAsync(storage, DelunoDatabaseNames.Series,
            $"INSERT INTO season_entries (id, series_id, season_number, created_utc, updated_utc) VALUES ('season-1', '{show.Id}', 1, '2026-10-08T00:00:00Z', '2026-10-08T00:00:00Z')");
        await ExecuteAsync(storage, DelunoDatabaseNames.Series,
            $"INSERT INTO episode_entries (id, series_id, season_number, episode_number, monitored, created_utc, updated_utc) VALUES ('ep-9', '{show.Id}', 1, 9, 1, '2026-10-08T00:00:00Z', '2026-10-08T00:00:00Z')");
        await ExecuteAsync(storage, DelunoDatabaseNames.Series,
            "INSERT INTO episode_subtitle_state (episode_id, language, source, created_utc, updated_utc) VALUES ('ep-9', 'en', 'sidecar', '2026-10-08T00:00:00Z', '2026-10-08T00:00:00Z')");
        await notifier.FlushAsync(DelunoDatabaseNames.Series, CancellationToken.None);

        var announced = Assert.Single(recorder.Titles);
        Assert.Equal(("Series", show.Id), (announced.Type, announced.Id));
        Assert.Equal(["lib-tv"], announced.Libraries);
        Assert.Equal([("Episode", "ep-9")], recorder.Entities.ToArray());
    }

    [Fact]
    public async Task A_change_to_a_film_that_names_no_library_is_announced_to_every_library_that_holds_it()
    {
        using var storage = TestStorage.Create();
        var movies = await MoviesAsync(storage);
        var film = await movies.AddAsync(new CreateMovieRequest("Heat", 1995, null), CancellationToken.None);
        await movies.EnsureWantedStateAsync(film.Id, "lib-1", "missing", "Wanted", hasFile: false, null, "WEB 1080p", false, CancellationToken.None);
        await movies.EnsureWantedStateAsync(film.Id, "lib-2", "missing", "Wanted", hasFile: false, null, "WEB 1080p", false, CancellationToken.None);
        var recorder = new Recorder();
        var notifier = NotifierFor(storage, recorder);
        await notifier.FlushAsync(DelunoDatabaseNames.Movies, CancellationToken.None);
        recorder.Clear();

        await ExecuteAsync(storage, DelunoDatabaseNames.Movies, $"UPDATE movie_entries SET title = 'Heat (Director''s Cut)' WHERE id = '{film.Id}'");
        await notifier.FlushAsync(DelunoDatabaseNames.Movies, CancellationToken.None);

        var announced = Assert.Single(recorder.Titles);
        Assert.Equal(("Movie", film.Id), (announced.Type, announced.Id));
        Assert.Equal(["lib-1", "lib-2"], announced.Libraries.Order().ToArray());
    }

    private static async Task ExecuteAsync(TestStorage storage, string database, string sql)
    {
        await using var connection = await storage.Factory.OpenConnectionAsync(database, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FailsOnce : NullRealtimeEventPublisher, IRealtimeEventPublisher
    {
        private int _calls;

        public ConcurrentQueue<string> Titles { get; } = new();

        public Task PublishTitleChangedAsync(string entityType, string entityId, IReadOnlyCollection<string> libraryIds, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                throw new InvalidOperationException("The hub is not ready.");
            }

            Titles.Enqueue(entityId);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task The_loop_waits_for_a_write_then_a_quiet_window_then_announces_and_a_flush_that_fails_does_not_stop_it()
    {
        using var storage = TestStorage.Create();
        var movies = await MoviesAsync(storage);
        var publisher = new FailsOnce();
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Now);
        var notifier = new TitleChangeNotifier(storage.Factory, publisher, NullLogger<TitleChangeNotifier>.Instance, clock);
        await notifier.FlushAsync(DelunoDatabaseNames.Movies, CancellationToken.None);
        await notifier.StartAsync(CancellationToken.None);

        var first = await movies.AddAsync(new CreateMovieRequest("Heat", 1995, null), CancellationToken.None);
        await Task.Delay(100); // reason: absence has to be watched for; nothing may be announced before the quiet window has passed on the clock
        Assert.Empty(publisher.Titles);

        // The window runs out; the first flush fails and is skipped.
        await Wait.UntilAsync(() => { clock.Advance(TimeSpan.FromMilliseconds(50)); return clock.GetUtcNow() >= Now.AddMilliseconds(250) && publisher.Titles.IsEmpty; }, "the quiet window to pass");
        await Task.Delay(100); // reason: the failed flush happens on the loop's own thread; there is no signal to await
        Assert.Empty(publisher.Titles);

        // The next write is announced after its own window.
        var second = await movies.AddAsync(new CreateMovieRequest("Ronin", 1998, null), CancellationToken.None);
        await Wait.UntilAsync(() => { clock.Advance(TimeSpan.FromMilliseconds(50)); return !publisher.Titles.IsEmpty; }, "the second write to be announced after the failed flush");
        await notifier.StopAsync(CancellationToken.None);

        Assert.Contains(second.Id, publisher.Titles);
        Assert.DoesNotContain(first.Id, publisher.Titles);
    }
}
