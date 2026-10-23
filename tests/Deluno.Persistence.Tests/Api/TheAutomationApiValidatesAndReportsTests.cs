using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// What the bulk add-a-title and episode-sync calls refuse and say (coverage register, engines): the envelope is checked before anything is
/// written, each item is checked on its own and an invalid one is reported without stopping the rest, a title that is already there is
/// reported and never added twice, a dry run changes and queues nothing, and a library is the one the caller named or the first of the
/// kind, never every library. Refs #1296.
/// </summary>
public sealed class TheAutomationApiValidatesAndReportsTests
{
    private const string Catalogue = "/api/automation/catalogue/bulk";

    private static async Task<string> LibraryAsync(ApplicationTestHost app, string mediaType, string name)
    {
        var root = Path.Combine(app.DataRoot, "libraries", name.Replace(' ', '-').ToLowerInvariant());
        Directory.CreateDirectory(root);
        using var response = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name, mediaType, purpose = "collection", rootPath = root, importWorkflow = "copy",
            autoSearchEnabled = false, missingSearchEnabled = false, upgradeSearchEnabled = false
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
    }

    private static async Task<long> MoviesAsync(ApplicationTestHost app) => await ApiScenes.ScalarAsync(app, "movies", "SELECT COUNT(*) FROM movie_entries;");

    private static async Task<long> RefreshJobsAsync(ApplicationTestHost app)
        => (await app.Services.GetRequiredService<IJobQueueRepository>().ListAsync(500, CancellationToken.None)).Count(job => job.JobType == "movies.catalog.refresh");

    [Fact]
    public async Task A_title_that_is_already_there_is_reported_already_there_and_is_not_added_or_refreshed_a_second_time()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await LibraryAsync(app, "movies", "Films");
        var film = new { clientItemId = "a", mediaType = "movie", title = "Example Film", year = 2024, imdbId = "tt0000001" };
        (await app.Client.PostAsJsonAsync(Catalogue, new { items = new[] { film } })).EnsureSuccessStatusCode();
        var jobsAfterFirst = await RefreshJobsAsync(app);

        using var again = await app.Client.PostAsJsonAsync(Catalogue, new { items = new[] { film } });

        again.EnsureSuccessStatusCode();
        var body = await again.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("createdCount").GetInt32());
        Assert.Equal(1, body.GetProperty("existingCount").GetInt32());
        Assert.Equal("already-exists", body.GetProperty("items")[0].GetProperty("status").GetString());
        Assert.Equal(1, await MoviesAsync(app));
        Assert.Equal(jobsAfterFirst, await RefreshJobsAsync(app));
    }

    [Fact]
    public async Task A_dry_run_says_would_create_or_already_there_and_writes_and_queues_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await LibraryAsync(app, "movies", "Films");
        (await app.Client.PostAsJsonAsync(Catalogue, new { items = new[] { new { clientItemId = "old", mediaType = "movie", title = "Old Film", year = 2001 } } })).EnsureSuccessStatusCode();
        var moviesBefore = await MoviesAsync(app);
        var jobsBefore = await RefreshJobsAsync(app);

        using var response = await app.Client.PostAsJsonAsync(Catalogue, new
        {
            dryRun = true,
            items = new[]
            {
                new { clientItemId = "old", mediaType = "movie", title = "Old Film", year = 2001 },
                new { clientItemId = "new", mediaType = "movie", title = "New Film", year = 2025 }
            }
        });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("dryRun").GetBoolean());
        var statuses = body.GetProperty("items").EnumerateArray().ToDictionary(item => item.GetProperty("clientItemId").GetString()!, item => item.GetProperty("status").GetString());
        Assert.Equal("already-exists", statuses["old"]);
        Assert.Equal("would-create", statuses["new"]);
        Assert.Equal(moviesBefore, await MoviesAsync(app));
        Assert.Equal(jobsBefore, await RefreshJobsAsync(app));
    }

    [Theory]
    [InlineData("music", "Example", 2024, "Media type must be movie/movies or tv/series.")]
    [InlineData("movie", " ", 2024, "Title is required.")]
    [InlineData("movie", "Example", 1869, "Year must be between 1870 and 2200.")]
    [InlineData("movie", "Example", 2201, "Year must be between 1870 and 2200.")]
    public async Task An_item_that_is_not_right_is_reported_invalid_with_the_reason_and_the_others_in_the_batch_still_go_in(string mediaType, string title, int year, string reason)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await LibraryAsync(app, "movies", "Films");

        using var response = await app.Client.PostAsJsonAsync(Catalogue, new
        {
            items = new[]
            {
                new { clientItemId = "bad", mediaType, title, year },
                new { clientItemId = "good", mediaType = "movie", title = "Good Film", year = 2024 }
            }
        });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToDictionary(item => item.GetProperty("clientItemId").GetString()!);
        Assert.Equal("invalid", items["bad"].GetProperty("status").GetString());
        Assert.Contains(reason, items["bad"].GetProperty("error").GetString());
        Assert.Equal("created", items["good"].GetProperty("status").GetString());
        Assert.Equal(1, await MoviesAsync(app));
    }

    [Fact]
    public async Task A_library_that_is_not_a_library_of_the_items_kind_and_show_settings_that_are_not_known_are_reported_invalid()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await LibraryAsync(app, "movies", "Films");
        var shows = await LibraryAsync(app, "tv", "Shows");

        using var response = await app.Client.PostAsJsonAsync(Catalogue, new
        {
            items = new object[]
            {
                new { clientItemId = "wrong-library", mediaType = "movie", title = "A", year = 2024, libraryId = shows },
                new { clientItemId = "no-library", mediaType = "movie", title = "B", year = 2024, libraryId = "no-such-library" },
                new { clientItemId = "type", mediaType = "tv", title = "C", year = 2024, seriesType = "weekly" },
                new { clientItemId = "scheme", mediaType = "tv", title = "D", year = 2024, numberingScheme = "roman" },
                new { clientItemId = "source", mediaType = "tv", title = "E", year = 2024, numberingSource = "guess" },
                new { clientItemId = "episodes-on-a-film", mediaType = "movie", title = "F", year = 2024, episodes = new[] { new { seasonNumber = 1, episodeNumber = 1 } } }
            }
        });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToDictionary(item => item.GetProperty("clientItemId").GetString()!);
        Assert.All(items.Values, item => Assert.Equal("invalid", item.GetProperty("status").GetString()));
        Assert.Contains("Library id does not identify a library for this media type.", items["wrong-library"].GetProperty("error").GetString());
        Assert.Contains("Library id does not identify a library for this media type.", items["no-library"].GetProperty("error").GetString());
        Assert.Contains("Series type must be standard, daily, or anime.", items["type"].GetProperty("error").GetString());
        Assert.Contains("Numbering scheme must be standard, airdate, absolute, or scene.", items["scheme"].GetProperty("error").GetString());
        Assert.Contains("Numbering source must be provider or owner.", items["source"].GetProperty("error").GetString());
        Assert.Contains("Explicit episodes can only be supplied for a TV item.", items["episodes-on-a-film"].GetProperty("error").GetString());
        Assert.Equal(0, await MoviesAsync(app));
    }

    [Fact]
    public async Task The_request_itself_is_checked_before_anything_is_written()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await LibraryAsync(app, "movies", "Films");
        var many = Enumerable.Range(0, 251).Select(index => new { clientItemId = $"item-{index}", mediaType = "movie", title = $"Film {index}", year = 2024 }).ToArray();

        var none = await ProblemAsync(await app.Client.PostAsJsonAsync(Catalogue, new { items = Array.Empty<object>() }));
        var tooMany = await ProblemAsync(await app.Client.PostAsJsonAsync(Catalogue, new { items = many }));
        var twice = await ProblemAsync(await app.Client.PostAsJsonAsync(Catalogue, new
        {
            items = new[] { new { clientItemId = "Same", mediaType = "movie", title = "A", year = 2024 }, new { clientItemId = "same", mediaType = "movie", title = "B", year = 2024 } }
        }));
        var longKey = await ProblemAsync(await app.Client.PostAsJsonAsync(Catalogue, new
        {
            idempotencyKey = new string('k', 201),
            items = new[] { new { clientItemId = "a", mediaType = "movie", title = "A", year = 2024 } }
        }));

        Assert.Equal("Submit at least one catalogue item.", none.GetProperty("items")[0].GetString());
        Assert.Contains("at most 250", tooMany.GetProperty("items")[0].GetString());
        Assert.Contains("Client item ids must be unique: Same", twice.GetProperty("items")[0].GetString());
        Assert.Contains("200", longKey.GetProperty("idempotencyKey")[0].GetString());
        Assert.Equal(0, await MoviesAsync(app));
    }

    [Fact]
    public async Task A_title_goes_to_the_library_the_caller_named_and_otherwise_to_the_first_of_its_kind_and_never_to_every_library()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var first = await LibraryAsync(app, "movies", "First films");
        var second = await LibraryAsync(app, "movies", "Second films");
        await LibraryAsync(app, "tv", "Shows");

        using var named = await app.Client.PostAsJsonAsync(Catalogue, new { items = new[] { new { clientItemId = "n", mediaType = "movie", title = "Named", year = 2024, libraryId = second } } });
        using var unnamed = await app.Client.PostAsJsonAsync(Catalogue, new { items = new[] { new { clientItemId = "u", mediaType = "movie", title = "Unnamed", year = 2024 } } });

        named.EnsureSuccessStatusCode();
        unnamed.EnsureSuccessStatusCode();
        var namedId = (await named.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items")[0].GetProperty("entityId").GetString()!;
        var unnamedId = (await unnamed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items")[0].GetProperty("entityId").GetString()!;
        Assert.Equal(1, await ApiScenes.ScalarAsync(app, "movies", $"SELECT COUNT(*) FROM movie_wanted_state WHERE movie_id = '{namedId}';"));
        Assert.Equal(second, await ApiScenes.TextAsync(app, "movies", $"SELECT library_id FROM movie_wanted_state WHERE movie_id = '{namedId}';"));
        Assert.Equal(1, await ApiScenes.ScalarAsync(app, "movies", $"SELECT COUNT(*) FROM movie_wanted_state WHERE movie_id = '{unnamedId}';"));
        Assert.Equal(first, await ApiScenes.TextAsync(app, "movies", $"SELECT library_id FROM movie_wanted_state WHERE movie_id = '{unnamedId}';"));
    }

    [Fact]
    public async Task Episodes_for_a_show_that_is_not_there_say_so_in_plain_words_and_episodes_that_are_not_right_are_reported_one_by_one()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await LibraryAsync(app, "tv", "Shows");
        using var missing = await app.Client.PostAsJsonAsync("/api/automation/series/no-such-show/episodes/bulk", new { episodes = new[] { new { seasonNumber = 1, episodeNumber = 1 } } });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("The requested TV show does not exist in Deluno.", (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());

        using var added = await app.Client.PostAsJsonAsync(Catalogue, new { items = new[] { new { clientItemId = "s", mediaType = "tv", title = "Example Show", year = 2020 } } });
        added.EnsureSuccessStatusCode();
        var seriesId = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items")[0].GetProperty("entityId").GetString()!;

        using var response = await app.Client.PostAsJsonAsync($"/api/automation/series/{seriesId}/episodes/bulk", new
        {
            episodes = new object[]
            {
                new { clientItemId = "ok", seasonNumber = 1, episodeNumber = 1, title = "Pilot" },
                new { clientItemId = "negative-season", seasonNumber = -1, episodeNumber = 1 },
                new { clientItemId = "no-episode", seasonNumber = 1, episodeNumber = 0 },
                new { clientItemId = "absolute", seasonNumber = 1, episodeNumber = 3, absoluteNumber = 0 },
                new { clientItemId = "half-scene", seasonNumber = 1, episodeNumber = 4, sceneSeasonNumber = 1 },
                new { clientItemId = "source", seasonNumber = 1, episodeNumber = 5, numberingSource = "guess" }
            }
        });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("episodes").EnumerateArray().ToDictionary(item => item.GetProperty("clientItemId").GetString()!);
        Assert.Equal("synced", items["ok"].GetProperty("status").GetString());
        Assert.Equal(5, body.GetProperty("invalidCount").GetInt32());
        Assert.Contains("Season number cannot be negative.", items["negative-season"].GetProperty("error").GetString());
        Assert.Contains("Episode number must be greater than zero.", items["no-episode"].GetProperty("error").GetString());
        Assert.Contains("Absolute number must be greater than zero", items["absolute"].GetProperty("error").GetString());
        Assert.Contains("Scene season and scene episode numbers must be supplied together.", items["half-scene"].GetProperty("error").GetString());
        Assert.Contains("Numbering source must be provider or owner.", items["source"].GetProperty("error").GetString());
        Assert.Equal(1, body.GetProperty("episodesAdded").GetInt32());
    }

    [Fact]
    public async Task The_episode_request_is_checked_for_size_and_repeats_and_a_dry_run_says_would_sync_and_writes_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await LibraryAsync(app, "tv", "Shows");
        using var added = await app.Client.PostAsJsonAsync(Catalogue, new { items = new[] { new { clientItemId = "s", mediaType = "tv", title = "Example Show", year = 2020 } } });
        var seriesId = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items")[0].GetProperty("entityId").GetString()!;
        var route = $"/api/automation/series/{seriesId}/episodes/bulk";
        var many = Enumerable.Range(1, 1001).Select(number => new { seasonNumber = 1, episodeNumber = number }).ToArray();

        var none = await ProblemAsync(await app.Client.PostAsJsonAsync(route, new { episodes = Array.Empty<object>() }));
        var tooMany = await ProblemAsync(await app.Client.PostAsJsonAsync(route, new { episodes = many }));
        var repeated = await ProblemAsync(await app.Client.PostAsJsonAsync(route, new { episodes = new[] { new { seasonNumber = 1, episodeNumber = 1 }, new { seasonNumber = 1, episodeNumber = 1 } } }));
        var episodesBefore = await ApiScenes.ScalarAsync(app, "series", "SELECT COUNT(*) FROM episode_entries;");

        using var dry = await app.Client.PostAsJsonAsync(route, new { dryRun = true, episodes = new[] { new { clientItemId = "a", seasonNumber = 1, episodeNumber = 1, title = "Pilot" } } });

        Assert.Equal("Submit at least one episode.", none.GetProperty("episodes")[0].GetString());
        Assert.Contains("at most 1000", tooMany.GetProperty("episodes")[0].GetString());
        Assert.Contains("Episode numbers must be unique: S01E01", repeated.GetProperty("episodes")[0].GetString());
        dry.EnsureSuccessStatusCode();
        var body = await dry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("dryRun").GetBoolean());
        Assert.Equal("would-sync", body.GetProperty("episodes")[0].GetProperty("status").GetString());
        Assert.Equal(0, body.GetProperty("episodesAdded").GetInt32());
        Assert.Equal(episodesBefore, await ApiScenes.ScalarAsync(app, "series", "SELECT COUNT(*) FROM episode_entries;"));
    }

    [Theory]
    [InlineData("24h", 24)]
    [InlineData("7d", 168)]
    [InlineData("30d", 720)]
    [InlineData("", 24)]
    [InlineData("a-year", 24)]
    public async Task The_stats_window_is_24_hours_a_week_or_a_month_and_anything_else_means_24_hours(string window, int hours)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var stats = await app.Client.GetFromJsonAsync<JsonElement>($"/api/stats?window={window}");

        Assert.Equal(hours, stats.GetProperty("windowHours").GetInt32());
        var since = stats.GetProperty("sinceUtc").GetDateTimeOffset();
        var generated = stats.GetProperty("generatedUtc").GetDateTimeOffset();
        Assert.InRange((DateTimeOffset.UtcNow - since).TotalHours, hours - 0.1, hours + 0.1);
        Assert.True(generated > DateTimeOffset.UtcNow.AddMinutes(-5));
    }
}
