using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Jobs.Data;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// What another program can do through the external integration API: add titles and episodes in bulk, safely retried with an
/// idempotency key, and ask for library searches. Everything goes through the real host and its real databases.
/// </summary>
public sealed class ExternalAutomationApiTests
{
    [Fact]
    public async Task A_bulk_added_film_is_created_wanted_in_the_first_film_library_and_queues_its_refresh()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var filmLibrary = await CreateLibraryAsync(app, "movies", "Films");
        await CreateLibraryAsync(app, "movies", "More films");

        using var response = await app.Client.PostAsJsonAsync("/api/automation/catalogue/bulk", new
        {
            items = new[] { new { clientItemId = "a", mediaType = "movie", title = "Example Film", year = 2024, imdbId = "tt0000001" } }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("createdCount").GetInt32());
        var item = body.GetProperty("items")[0];
        Assert.Equal("created", item.GetProperty("status").GetString());
        var movieId = item.GetProperty("entityId").GetString()!;

        var movie = await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{movieId}");
        Assert.Equal("Example Film", movie.GetProperty("title").GetString());

        var wanted = await app.Services.GetRequiredService<IMovieCatalogRepository>()
            .GetMovieWantedStateAsync(movieId, filmLibrary, CancellationToken.None);
        Assert.NotNull(wanted);
        Assert.Equal(filmLibrary, wanted!.LibraryId);

        var refresh = Assert.Single(await app.Services.GetRequiredService<IJobQueueRepository>().ListAsync(50, CancellationToken.None),
            job => job.JobType == "movies.catalog.refresh");
        Assert.Equal(item.GetProperty("refreshJobId").GetString(), refresh.Id);
    }

    [Fact]
    public async Task A_bulk_added_show_keeps_its_explicit_episodes_and_its_anime_numbering()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await CreateLibraryAsync(app, "tv", "Shows");

        using var response = await app.Client.PostAsJsonAsync("/api/automation/catalogue/bulk", new
        {
            items = new[]
            {
                new
                {
                    clientItemId = "s",
                    mediaType = "tv",
                    title = "Example Show",
                    year = 2023,
                    seriesType = "anime",
                    episodes = new[]
                    {
                        new { seasonNumber = 1, episodeNumber = 1, title = "One" },
                        new { seasonNumber = 1, episodeNumber = 2, title = "Two" }
                    }
                }
            }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items")[0];
        Assert.Equal("created", item.GetProperty("status").GetString());
        Assert.Equal(2, item.GetProperty("episodesAdded").GetInt32());
        Assert.Equal(2, item.GetProperty("episodeCount").GetInt32());

        var show = await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/{item.GetProperty("entityId").GetString()}");
        Assert.Equal("anime", show.GetProperty("seriesType").GetString());
    }

    [Fact]
    public async Task Sending_the_same_batch_again_with_the_same_key_replays_the_answer_and_adds_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await CreateLibraryAsync(app, "movies", "Films");
        var batch = new { items = new[] { new { clientItemId = "a", mediaType = "movies", title = "Retry Film", year = 2022 } } };

        var first = await PostWithKeyAsync(app, batch, headerKey: "retry-1");
        var second = await PostWithKeyAsync(app, batch, headerKey: "retry-1");

        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal(HttpStatusCode.OK, second.Status);
        Assert.Equal(first.Text, second.Text);
        Assert.Contains("\"status\":\"created\"", second.Text);
        Assert.NotNull(await FindFilmAsync(app, "Retry Film", 2022));
    }

    [Fact]
    public async Task A_key_in_the_body_only_and_the_same_key_in_the_header_only_are_one_operation()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await CreateLibraryAsync(app, "movies", "Films");
        var items = new[] { new { clientItemId = "a", mediaType = "movies", title = "Same Key Film", year = 2022 } };

        var byBody = await PostWithKeyAsync(app, new { items, idempotencyKey = "one-key" }, headerKey: null);
        var byHeader = await PostWithKeyAsync(app, new { items }, headerKey: "one-key");

        Assert.Equal(HttpStatusCode.OK, byBody.Status);
        Assert.Equal(HttpStatusCode.OK, byHeader.Status);
        Assert.Equal(byBody.Text, byHeader.Text);
    }

    [Fact]
    public async Task A_different_batch_under_a_used_key_is_a_conflict_and_a_header_that_disagrees_with_the_body_is_refused()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await CreateLibraryAsync(app, "movies", "Films");
        var batch = new { items = new[] { new { clientItemId = "a", mediaType = "movies", title = "First Film", year = 2022 } } };
        var other = new { items = new[] { new { clientItemId = "a", mediaType = "movies", title = "Second Film", year = 2021 } } };
        await PostWithKeyAsync(app, batch, headerKey: "used-key");

        var changed = await PostWithKeyAsync(app, other, headerKey: "used-key");
        var mismatch = await PostWithKeyAsync(app, new { other.items, idempotencyKey = "body-key" }, headerKey: "header-key");

        Assert.Equal(HttpStatusCode.Conflict, changed.Status);
        Assert.Contains("different operation or request body", changed.Text);
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.Status);
        Assert.Contains("must match", mismatch.Text);
        Assert.Null(await FindFilmAsync(app, "Second Film", 2021));
    }

    [Fact]
    public async Task Episodes_posted_to_a_known_show_are_added_and_a_changed_title_on_a_second_post_is_an_update()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await CreateLibraryAsync(app, "tv", "Shows");
        var added = await app.Client.PostAsJsonAsync("/api/automation/catalogue/bulk", new
        {
            items = new[] { new { clientItemId = "s", mediaType = "tv", title = "Sync Show", year = 2020 } }
        });
        var seriesId = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items")[0].GetProperty("entityId").GetString();

        var first = await app.Client.PostAsJsonAsync($"/api/automation/series/{seriesId}/episodes/bulk", new
        {
            episodes = new[]
            {
                new { clientItemId = "1", seasonNumber = 1, episodeNumber = 1, title = "A" },
                new { clientItemId = "2", seasonNumber = 1, episodeNumber = 2, title = "B" },
                new { clientItemId = "3", seasonNumber = 1, episodeNumber = 3, title = "C" }
            }
        });
        var second = await app.Client.PostAsJsonAsync($"/api/automation/series/{seriesId}/episodes/bulk", new
        {
            episodes = new[] { new { clientItemId = "1", seasonNumber = 1, episodeNumber = 1, title = "A, renamed" } }
        });

        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, firstBody.GetProperty("syncedCount").GetInt32());
        Assert.Equal(3, firstBody.GetProperty("episodesAdded").GetInt32());
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, secondBody.GetProperty("episodesAdded").GetInt32());
        Assert.Equal(1, secondBody.GetProperty("episodesUpdated").GetInt32());
    }

    [Fact]
    public async Task Episodes_for_a_show_Deluno_does_not_have_are_a_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        using var response = await app.Client.PostAsJsonAsync("/api/automation/series/not-a-show/episodes/bulk", new
        {
            episodes = new[] { new { seasonNumber = 1, episodeNumber = 1, title = "A" } }
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("movies")]
    [InlineData("movie")]
    public async Task Trigger_refresh_for_films_asks_a_search_only_in_the_film_libraries(string mediaType)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var films = await CreateLibraryAsync(app, "movies", "Films");
        await CreateLibraryAsync(app, "tv", "Shows");

        using var response = await app.Client.PostAsJsonAsync("/api/integrations/external/trigger-refresh", new { mediaType, reason = "test" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("enqueued").GetInt32());
        Assert.Equal([films], await LibrariesWithASearchAskedAsync(app));
    }

    [Theory]
    [InlineData("tv")]
    [InlineData("series")]
    [InlineData("shows")]
    public async Task Trigger_refresh_for_shows_asks_a_search_only_in_the_TV_libraries(string mediaType)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await CreateLibraryAsync(app, "movies", "Films");
        var shows = await CreateLibraryAsync(app, "tv", "Shows");

        using var response = await app.Client.PostAsJsonAsync("/api/integrations/external/trigger-refresh", new { mediaType });

        Assert.Equal(1, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("enqueued").GetInt32());
        Assert.Equal([shows], await LibrariesWithASearchAskedAsync(app));
    }

    [Fact]
    public async Task Trigger_refresh_with_no_media_type_asks_a_search_in_every_library()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var films = await CreateLibraryAsync(app, "movies", "Films");
        var shows = await CreateLibraryAsync(app, "tv", "Shows");

        using var response = await app.Client.PostAsJsonAsync("/api/integrations/external/trigger-refresh", new { });

        Assert.Equal(2, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("enqueued").GetInt32());
        Assert.Equal(new[] { films, shows }.Order().ToArray(), await LibrariesWithASearchAskedAsync(app));
    }

    [Fact]
    public async Task The_missing_movies_command_starts_a_search_in_the_film_libraries_only_and_says_how_many()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var films = await CreateLibraryAsync(app, "movies", "Films");
        await CreateLibraryAsync(app, "tv", "Shows");

        using var response = await app.Client.PostAsJsonAsync("/api/command", new { name = "MissingMoviesSearch" });

        var command = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("Started a search in 1 library.", command.ToString());
        Assert.Equal([films], await LibrariesWithASearchAskedAsync(app));
    }

    [Theory]
    [InlineData("MissingEpisodeSearch")]
    [InlineData("MissingSeriesSearch")]
    public async Task The_missing_episode_command_starts_a_search_in_the_TV_libraries_only(string name)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await CreateLibraryAsync(app, "movies", "Films");
        var shows = await CreateLibraryAsync(app, "tv", "Shows");

        using var response = await app.Client.PostAsJsonAsync("/api/command", new { name });

        var command = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("Started a search in 1 library.", command.ToString());
        Assert.Equal([shows], await LibrariesWithASearchAskedAsync(app));
    }

    private static async Task<string[]> LibrariesWithASearchAskedAsync(ApplicationTestHost app)
    {
        var states = await app.Services.GetRequiredService<IJobQueueRepository>().ListLibraryAutomationStatesAsync(CancellationToken.None);
        return states.Values.Where(state => state.SearchRequested).Select(state => state.LibraryId).Order().ToArray();
    }

    private static Task<string?> FindFilmAsync(ApplicationTestHost app, string title, int year)
        => app.Services.GetRequiredService<IMovieCatalogRepository>().FindExistingIdAsync(title, year, null, null, null, CancellationToken.None);

    private static async Task<(HttpStatusCode Status, string Text)> PostWithKeyAsync(ApplicationTestHost app, object batch, string? headerKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/automation/catalogue/bulk") { Content = JsonContent.Create(batch) };
        if (headerKey is not null)
        {
            request.Headers.Add("Idempotency-Key", headerKey);
        }

        using var response = await app.Client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> CreateLibraryAsync(ApplicationTestHost app, string mediaType, string name)
    {
        var root = Path.Combine(app.DataRoot, "libraries", name.Replace(' ', '-').ToLowerInvariant());
        Directory.CreateDirectory(root);
        using var response = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name,
            mediaType,
            purpose = "collection",
            rootPath = root,
            importWorkflow = "copy",
            autoSearchEnabled = false,
            missingSearchEnabled = false,
            upgradeSearchEnabled = false
        });
        Assert.True(response.IsSuccessStatusCode, $"POST /api/libraries/ returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }
}
