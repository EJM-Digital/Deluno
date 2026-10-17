using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// What the Dashboard's Live and System tabs ask the server for (#1282), read over HTTP from a real, migrated set of
/// SQLite files with real films and shows on a real temp folder: the titles of the cards, the episodes about to air, the
/// indexers' scoreboard for today and the count of what needs a person.
/// </summary>
public sealed class DashboardLiveReadsOverHttpTests
{
    private static string Iso(DateTimeOffset when) => when.ToString("O", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Card_titles_for_films_come_back_in_one_request_and_a_removed_or_unknown_film_is_simply_absent()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var kept = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021);
        var removed = await ApiScenes.FilmOnDiskAsync(app, "Alien", 1979, libraryId: kept.LibraryId);
        var gone = await app.Client.DeleteAsync($"/api/movies/{removed.MovieId}");
        Assert.True(gone.IsSuccessStatusCode, await gone.Content.ReadAsStringAsync());

        var titles = await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/titles?ids={kept.MovieId},{removed.MovieId},not-a-film");

        // Asking for titles reads the stored rows only: the films' files and folders on disk are untouched.
        Assert.True(File.Exists(kept.FilePath));
        Assert.True(Directory.Exists(removed.FolderPath));

        var all = titles.EnumerateObject().ToArray();
        var only = Assert.Single(all);
        Assert.Equal(kept.MovieId, only.Name);
        Assert.Equal("Dune", only.Value.GetProperty("title").GetString());
        Assert.Equal(2021, only.Value.GetProperty("year").GetInt32());
        Assert.True(only.Value.TryGetProperty("posterUrl", out _));
    }

    [Fact]
    public async Task Card_titles_for_shows_and_their_episodes_come_back_in_one_request_with_the_season_and_episode_number()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var show = await ApiScenes.ShowOnDiskAsync(app, "The Wire", 2002);

        var titles = await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/titles?ids=unknown-show&episodeIds={show.EpisodeId},unknown-episode");

        // Asking for titles reads the stored rows only: the episode's file on disk is untouched.
        Assert.True(File.Exists(show.FilePath));

        // Asking for the episode brings its show with it, and the unknown ones are absent.
        var shows = titles.GetProperty("series").EnumerateObject().ToArray();
        var only = Assert.Single(shows);
        Assert.Equal(show.SeriesId, only.Name);
        Assert.Equal("The Wire", only.Value.GetProperty("title").GetString());
        Assert.Equal(2002, only.Value.GetProperty("year").GetInt32());
        var episode = Assert.Single(titles.GetProperty("episodes").EnumerateObject());
        Assert.Equal(show.EpisodeId, episode.Name);
        Assert.Equal(show.SeriesId, episode.Value.GetProperty("seriesId").GetString());
        Assert.Equal(1, episode.Value.GetProperty("season").GetInt32());
        Assert.Equal(1, episode.Value.GetProperty("episode").GetInt32());
    }

    [Fact]
    public async Task Episodes_about_to_air_are_the_monitored_ones_inside_the_window_soonest_first_and_no_more_than_asked_for()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var show = await ApiScenes.ShowOnDiskAsync(app, "The Wire", 2002);
        var now = DateTimeOffset.UtcNow;
        string Episode(string id, int number, DateTimeOffset? airs, int monitored) =>
            $"INSERT INTO episode_entries (id, series_id, season_number, episode_number, title, monitored, air_date_utc, created_utc, updated_utc) VALUES ('{id}', '{show.SeriesId}', 3, {number}, 'Episode {number}', {monitored}, {(airs is null ? "NULL" : $"'{Iso(airs.Value)}'")}, '{Iso(now)}', '{Iso(now)}');";
        await ApiScenes.ExecuteAsync(app, DelunoDatabaseNames.Series,
            Episode("later", 2, now.AddDays(5), 1) +
            Episode("soon", 1, now.AddHours(30), 1) +
            Episode("unmonitored", 3, now.AddHours(40), 0) +
            Episode("too-far", 4, now.AddDays(12), 1) +
            Episode("already-aired", 5, now.AddDays(-1), 1) +
            Episode("no-date", 6, null, 1));

        var week = await app.Client.GetFromJsonAsync<JsonElement>("/api/series/upcoming?take=20&hours=168");
        var one = await app.Client.GetFromJsonAsync<JsonElement>("/api/series/upcoming?take=1&hours=168");

        Assert.Equal(["soon", "later"], week.EnumerateArray().Select(item => item.GetProperty("episodeId").GetString()!).ToArray());
        var first = week[0];
        Assert.Equal("The Wire", first.GetProperty("title").GetString());
        Assert.Equal(3, first.GetProperty("seasonNumber").GetInt32());
        Assert.Equal(1, first.GetProperty("episodeNumber").GetInt32());
        Assert.Equal(["soon"], one.EnumerateArray().Select(item => item.GetProperty("episodeId").GetString()!).ToArray());
    }

    [Fact]
    public async Task Todays_scoreboard_counts_only_todays_queries_with_failures_results_and_average_time_and_keeps_a_removed_indexer_by_its_stored_name()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var connections = app.Services.GetRequiredService<IConnectionsRepository>();
        var live = await connections.CreateIndexerAsync(new CreateIndexerRequest("Live", "torznab", "public", "http://live.invalid/api", null, 25, "2000", "", "both", IsEnabled: true), CancellationToken.None);
        var stats = app.Services.GetRequiredService<IIndexerQueryStatsRepository>();
        var now = DateTimeOffset.UtcNow;
        IndexerQueryLogEntry Query(string id, string name, string outcome, int elapsed, int candidates, DateTimeOffset at)
            => new(id, name, "Wanted Film", "2000", "movies", "search", outcome, elapsed, candidates, at);
        await stats.RecordBatchAsync(
        [
            Query(live.Id, live.Name, "matched", 100, 4, now.AddMinutes(-30)),
            Query(live.Id, live.Name, "matched", 300, 6, now.AddMinutes(-20)),
            Query(live.Id, live.Name, "failed", 200, 0, now.AddMinutes(-10)),
            Query(live.Id, live.Name, "matched", 100, 50, now.AddDays(-3)),
            // an indexer that has since been removed: its queries are still counted, under the name stored with them
            Query("removed-indexer-id", "Old indexer", "matched", 80, 2, now.AddMinutes(-5))
        ], CancellationToken.None);

        var board = await app.Client.GetFromJsonAsync<JsonElement>("/api/indexers/scoreboard?days=1");

        var rows = board.GetProperty("indexers").EnumerateArray().ToDictionary(row => row.GetProperty("name").GetString()!);
        var today = rows["Live"];
        Assert.Equal(3, today.GetProperty("totalQueries").GetInt64());
        Assert.Equal(1, today.GetProperty("failedQueries").GetInt64());
        Assert.Equal(10, today.GetProperty("candidatesReturned").GetInt64());
        Assert.Equal(200, today.GetProperty("averageResponseMilliseconds").GetDouble(), 1);
        var removed = rows["Old indexer"];
        Assert.Equal(1, removed.GetProperty("totalQueries").GetInt64());
        Assert.Equal(80, removed.GetProperty("averageResponseMilliseconds").GetDouble(), 1);
        Assert.Equal(4, board.GetProperty("totalQueries").GetInt64());
    }

    [Fact]
    public async Task The_attention_count_over_HTTP_counts_a_failed_hand_off_an_open_recovery_case_and_a_down_indexer_once_each_with_their_ids()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var when = Iso(DateTimeOffset.UtcNow);
        var connections = app.Services.GetRequiredService<IConnectionsRepository>();
        var indexer = await connections.CreateIndexerAsync(new CreateIndexerRequest("Nyaa", "torznab", "public", "http://nyaa.invalid/api", null, 25, "2000", "", "both", IsEnabled: true), CancellationToken.None);
        await connections.UpdateIndexerHealthAsync(indexer.Id, "unreachable", "HTTP 504", null, null, CancellationToken.None);
        await ApiScenes.ExecuteAsync(app, DelunoDatabaseNames.Platform,
            $"INSERT INTO processor_handoffs (id, library_id, media_type, client_id, queue_item_id, release_name, source_path, source_key, processor_name, processor_connection_id, status, failure_message, created_utc, updated_utc) VALUES ('h1', 'lib', 'movies', 'c', 'q1', 'Metropolis.1927', '/r1', 'k1', 'Weir', NULL, '{ProcessorHandoffStatuses.Failed}', 'The processor timed out.', '{when}', '{when}');");
        await ApiScenes.ExecuteAsync(app, DelunoDatabaseNames.Movies,
            $"INSERT INTO movie_import_recovery_cases (id, title, failure_kind, summary, recommended_action, detected_utc, status) VALUES ('case1', 'Stalker', 'unmatched', 'Nothing in the library matched it.', 'Pick the film', '{when}', 'open');");

        var summary = await app.Client.GetFromJsonAsync<JsonElement>("/api/attention/summary");

        var rows = summary.GetProperty("rows").EnumerateArray().ToDictionary(row => row.GetProperty("id").GetString()!);
        Assert.Contains("handoff:h1", rows.Keys);
        Assert.Contains("recovery:movie:case1", rows.Keys);
        Assert.Contains($"indexer:{indexer.Id}", rows.Keys);
        Assert.Equal(rows.Count, summary.GetProperty("count").GetInt32());
        Assert.Equal("handoff", rows["handoff:h1"].GetProperty("kind").GetString());
        Assert.Equal("media", rows["handoff:h1"].GetProperty("section").GetString());
        Assert.Equal("connection", rows[$"indexer:{indexer.Id}"].GetProperty("kind").GetString());
        Assert.Equal("Nyaa isn’t answering", rows[$"indexer:{indexer.Id}"].GetProperty("title").GetString());
    }
}
