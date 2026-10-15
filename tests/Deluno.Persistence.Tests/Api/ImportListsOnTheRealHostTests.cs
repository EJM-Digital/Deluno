using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Integrations.Metadata;
using Deluno.Movies.Data;
using Deluno.Worker.Intake;
using Deluno.Persistence.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Import lists on the whole application with its own database files and a real library folder: the list is a feed served
/// over a real loopback port (a plain RSS feed is a file of titles, not another app's reply), Sync and Approve run the real
/// add path, and what they leave in the catalogue database is read back from the file.
/// </summary>
public sealed class ImportListsOnTheRealHostTests
{
    private static string Item(string title, string? category = null, DateTimeOffset? published = null)
        => $"<item><title>{title}</title>{(category is null ? "" : $"<category>{category}</category>")}{(published is null ? "" : $"<pubDate>{published.Value:R}</pubDate>")}</item>";

    private static string Feed(params string[] items) => $"<?xml version=\"1.0\"?><rss version=\"2.0\"><channel><title>list</title>{string.Concat(items)}</channel></rss>";

    private static MetadataSearchResult Card(string title, int year, string id, double? rating = null, params string[] genres)
        => new("tmdb", id, "movies", title, null, year, null, null, null, rating, [], genres, null, null);

    private static RecordingMetadataProvider Cards(params MetadataSearchResult[] cards) => new(cards, answerOnlyTheTitleAsked: true);

    /// <summary>Reads the catalogue database file itself, so a test shows what really reached the disk.</summary>
    private static async Task<string?> StoredAsync(ApplicationTestHost app, string database, string sql)
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = (SqliteConnection)await factory.OpenConnectionAsync(database, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    private static async Task<(WebApplication Server, string Url)> ServeAsync(Func<string> body)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var server = builder.Build();
        server.Run(async context =>
        {
            context.Response.ContentType = "application/rss+xml";
            await context.Response.WriteAsync(body(), Encoding.UTF8);
        });
        await server.StartAsync();
        var address = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return (server, $"{address}/list.xml");
    }

    private static async Task<string> CreateListAsync(ApplicationTestHost app, string libraryId, string url, object? extra = null, string name = "Weekend list")
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = name, ["provider"] = "rss", ["feedUrl"] = url, ["mediaType"] = "movies", ["libraryId"] = libraryId,
            ["searchOnAdd"] = false, ["isEnabled"] = true, ["syncIntervalHours"] = 12
        };
        if (extra is not null)
        {
            foreach (var property in extra.GetType().GetProperties())
            {
                body[property.Name] = property.GetValue(extra);
            }
        }

        var response = await app.Client.PostAsJsonAsync("/api/intake-sources", body);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task<IntakeSyncRunResult> SyncAsync(ApplicationTestHost app, string listId)
        => await app.Services.GetRequiredService<IIntakeSyncService>().RunAsync(listId, null, true, CancellationToken.None);

    private static async Task<string[]> MovieTitlesAsync(ApplicationTestHost app)
        => (await app.Services.GetRequiredService<IMovieCatalogRepository>().ListAsync(CancellationToken.None)).Select(movie => movie.Title).Order().ToArray();

    [Fact]
    public async Task Sync_adds_the_listed_films_to_a_real_library_as_wanted_records_where_each_came_from_and_asks_for_a_search_when_the_list_does()
    {
        await using var feed = new FeedHolder(Feed(Item("Arrival (2016)"), Item("Dune (2021)")));
        var (server, url) = await ServeAsync(() => feed.Body);
        await using var _ = server;
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<IMetadataProvider>(
            Cards(Card("Arrival", 2016, "329865", 7.9, "Drama"), Card("Dune", 2021, "438631", 8.0, "Science Fiction"))));
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var listId = await CreateListAsync(app, libraryId, url, new { searchOnAdd = true });

        var result = await SyncAsync(app, listId);

        Assert.Equal(("success", 2, 0, true), (result.Status, result.AddedCount, result.ErrorCount, result.SearchRequested));
        Assert.Equal(["Arrival", "Dune"], await MovieTitlesAsync(app));
        Assert.Equal("2", await StoredAsync(app, DelunoDatabaseNames.Movies, "SELECT count(*) FROM movie_entries WHERE title IN ('Arrival','Dune')"));
        Assert.Equal("2", await StoredAsync(app, DelunoDatabaseNames.Movies, $"SELECT count(*) FROM movie_wanted_state WHERE library_id = '{libraryId}'"));
        var movieId = (await app.Services.GetRequiredService<IMovieCatalogRepository>().ListAsync(CancellationToken.None)).Single(m => m.Title == "Arrival").Id;
        var origins = await app.Client.GetFromJsonAsync<JsonElement>($"/api/intake-title-origins?mediaType=movies&entityId={movieId}");
        // The list is named after its address by the server (connections never ask for a name), so ask what it was called.
        var listName = (await app.Client.GetFromJsonAsync<JsonElement>("/api/intake-sources")).EnumerateArray().Single().GetProperty("name").GetString();
        Assert.Equal(listName, Assert.Single(origins.EnumerateArray()).GetProperty("sourceName").GetString());
        var activity = await app.Client.GetStringAsync("/api/activity?take=50");
        Assert.Contains($"{listName} sync completed (success).", activity);
        Assert.Contains("added 2", activity);

        // A second sync finds both already there and adds nothing.
        var again = await SyncAsync(app, listId);
        Assert.Equal((0, 2), (again.AddedCount, again.DuplicateCount));
        Assert.Equal("2", await StoredAsync(app, DelunoDatabaseNames.Movies, "SELECT count(*) FROM movie_entries"));
    }

    [Fact]
    public async Task A_list_with_no_library_of_its_kind_adds_nothing_and_ends_as_a_failure_that_says_why()
    {
        await using var feed = new FeedHolder(Feed(Item("Arrival (2016)")));
        var (server, url) = await ServeAsync(() => feed.Body);
        await using var _ = server;
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<IMetadataProvider>(Cards(Card("Arrival", 2016, "329865"))));
        var listId = await CreateListAsync(app, "", url);

        var result = await SyncAsync(app, listId);

        Assert.Equal(("error", 0), (result.Status, result.AddedCount));
        Assert.Contains("No compatible target library exists", result.Summary);
        Assert.Empty(await MovieTitlesAsync(app));
        var stored = (await app.Client.GetFromJsonAsync<JsonElement>("/api/intake-sources")).EnumerateArray().Single();
        Assert.Equal("error", stored.GetProperty("lastSyncStatus").GetString());
    }

    [Fact]
    public async Task A_list_that_cannot_be_fetched_ends_as_failed_with_a_message_and_adds_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<IMetadataProvider>(Cards()));
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var listId = await CreateListAsync(app, libraryId, "http://127.0.0.1:1/list.xml");

        var result = await SyncAsync(app, listId);

        Assert.Equal(("error", 0), (result.Status, result.AddedCount));
        Assert.StartsWith("Provider fetch failed.", result.Summary);
        Assert.Empty(await MovieTitlesAsync(app));
    }

    [Fact]
    public async Task The_required_genre_filter_keeps_a_film_when_any_one_of_its_genres_matches_and_skips_the_rest_with_a_reason()
    {
        await using var feed = new FeedHolder(Feed(Item("Arrival (2016)", "Drama, Science Fiction"), Item("Superbad (2007)", "Comedy"), Item("Mystery Film (2010)")));
        var (server, url) = await ServeAsync(() => feed.Body);
        await using var _ = server;
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<IMetadataProvider>(Cards()));
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var listId = await CreateListAsync(app, libraryId, url, new { requiredGenres = "Horror, Science Fiction" });

        var result = await SyncAsync(app, listId);

        Assert.Equal(1, result.AddedCount);
        Assert.Equal(2, result.SkippedCount);
        Assert.Equal(["Arrival"], await MovieTitlesAsync(app));
        var activity = await app.Client.GetStringAsync("/api/activity?take=50");
        Assert.Contains("No required genres matched (Horror, Science Fiction)", activity);
        Assert.Contains("Missing genre metadata for required genre filter.", activity);
    }

    [Fact]
    public async Task The_minimum_year_and_maximum_age_filters_skip_older_films_with_their_own_reasons()
    {
        var now = DateTimeOffset.UtcNow;
        await using var feed = new FeedHolder(Feed(
            Item("New Film (2026)", published: now.AddDays(-3)),
            Item("Old Film (1999)", published: now.AddDays(-3)),
            Item("Stale Film (2026)", published: now.AddDays(-400))));
        var (server, url) = await ServeAsync(() => feed.Body);
        await using var _ = server;
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<IMetadataProvider>(Cards()));
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var listId = await CreateListAsync(app, libraryId, url, new { minimumYear = 2020, maximumAgeDays = 30 });

        var preview = await (await app.Client.PostAsync($"/api/intake-sources/{listId}/preview", null)).Content.ReadFromJsonAsync<JsonElement>();
        var reasons = preview.GetProperty("items").EnumerateArray().ToDictionary(item => item.GetProperty("title").GetString()!, item => (item.GetProperty("action").GetString(), item.GetProperty("reason").GetString()));
        Assert.Equal("would add", reasons["New Film"].Item1);
        Assert.Equal(("not eligible", "Below minimum year (2020)."), reasons["Old Film"]);
        Assert.Equal(("not eligible", "Older than maximum age (30 days)."), reasons["Stale Film"]);

        var result = await SyncAsync(app, listId);

        Assert.Equal(1, result.AddedCount);
        Assert.Equal(["New Film"], await MovieTitlesAsync(app));
        var activity = await app.Client.GetStringAsync("/api/activity?take=50");
        Assert.Contains("Below minimum year (2020).", activity);
        Assert.Contains("Older than maximum age (30 days).", activity);
    }

    [Fact]
    public async Task The_kids_audience_filter_guesses_the_audience_from_the_genres_when_the_list_gives_none()
    {
        await using var feed = new FeedHolder(Feed(Item("Toy Story (1995)", "Animation"), Item("Heat (1995)", "Crime")));
        var (server, url) = await ServeAsync(() => feed.Body);
        await using var _ = server;
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<IMetadataProvider>(Cards()));
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var listId = await CreateListAsync(app, libraryId, url, new { audience = "kids" });

        var result = await SyncAsync(app, listId);

        Assert.Equal(1, result.AddedCount);
        Assert.Equal(["Toy Story"], await MovieTitlesAsync(app));
        Assert.Contains("Audience 'any' did not match required audience 'kids'.", await app.Client.GetStringAsync("/api/activity?take=50"));
    }

    [Fact]
    public async Task Approve_refuses_no_entries_and_more_than_100_and_a_list_that_is_not_there_in_plain_words_and_adds_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<IMetadataProvider>(Cards()));
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var listId = await CreateListAsync(app, libraryId, "http://127.0.0.1:1/list.xml");

        var none = await app.Client.PostAsJsonAsync($"/api/intake-sources/{listId}/approve-preview", new { entries = Array.Empty<object>(), searchAfterAdd = false });
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Equal("Choose at least one preview entry to add.", (await none.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());

        var tooMany = await app.Client.PostAsJsonAsync($"/api/intake-sources/{listId}/approve-preview", new { entries = Enumerable.Range(0, 101).Select(i => new { title = $"Film {i}", year = 2000 }), searchAfterAdd = false });
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Equal("Choose no more than 100 preview entries at a time.", (await tooMany.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());

        var unknown = await app.Client.PostAsJsonAsync("/api/intake-sources/no-such-list/approve-preview", new { entries = new[] { new { title = "Arrival", year = 2016 } }, searchAfterAdd = false });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal("Import list not found.", (await unknown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
        Assert.Equal("0", await StoredAsync(app, DelunoDatabaseNames.Movies, "SELECT count(*) FROM movie_entries"));
    }

    [Fact]
    public async Task Approve_fetches_the_list_again_adds_only_the_chosen_entries_through_the_normal_path_and_says_how_many_of_how_many_in_Activity()
    {
        await using var feed = new FeedHolder(Feed(Item("Arrival (2016)"), Item("Dune (2021)"), Item("Heat (1995)")));
        var (server, url) = await ServeAsync(() => feed.Body);
        await using var _ = server;
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<IMetadataProvider>(
            Cards(Card("Arrival", 2016, "329865"), Card("Dune", 2021, "438631"), Card("Heat", 1995, "949"))));
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var listId = await CreateListAsync(app, libraryId, url);

        var approved = await app.Client.PostAsJsonAsync($"/api/intake-sources/{listId}/approve-preview", new
        {
            entries = new[] { new { title = "Arrival", year = 2016 }, new { title = "Heat", year = 1995 } },
            searchAfterAdd = true
        });

        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var summary = await approved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((2, 2, 2), (summary.GetProperty("selectedCount").GetInt32(), summary.GetProperty("matchedCount").GetInt32(), summary.GetProperty("addedCount").GetInt32()));
        Assert.True(summary.GetProperty("searchRequested").GetBoolean(), "ticking 'search after add' must ask for a search even though the list itself does not");
        Assert.Equal(["Arrival", "Heat"], await MovieTitlesAsync(app));
        Assert.Contains($"approved 2 of 2 previewed entries.", await app.Client.GetStringAsync("/api/activity?take=50"));
    }

    [Fact]
    public async Task Approve_without_search_after_add_adds_the_chosen_film_but_asks_for_no_search()
    {
        await using var feed = new FeedHolder(Feed(Item("Arrival (2016)")));
        var (server, url) = await ServeAsync(() => feed.Body);
        await using var _ = server;
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<IMetadataProvider>(Cards(Card("Arrival", 2016, "329865"))));
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var listId = await CreateListAsync(app, libraryId, url);

        var summary = await (await app.Client.PostAsJsonAsync($"/api/intake-sources/{listId}/approve-preview", new { entries = new[] { new { title = "Arrival", year = 2016 } }, searchAfterAdd = false })).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(1, summary.GetProperty("addedCount").GetInt32());
        Assert.False(summary.GetProperty("searchRequested").GetBoolean());
    }

    [Fact]
    public async Task An_excluded_title_is_not_added_again_by_the_list_and_allowing_it_again_lets_the_next_sync_add_it()
    {
        await using var feed = new FeedHolder(Feed(Item("Arrival (2016)"), Item("Dune (2021)")));
        var (server, url) = await ServeAsync(() => feed.Body);
        await using var _ = server;
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<IMetadataProvider>(
            Cards(Card("Arrival", 2016, "329865"), Card("Dune", 2021, "438631"))));
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var listId = await CreateListAsync(app, libraryId, url);
        var excluded = await app.Client.PostAsJsonAsync($"/api/intake-sources/{listId}/exclude-preview", new { title = "Arrival", year = 2016, durationDays = (int?)null, reason = "Not for us" });
        Assert.Equal(HttpStatusCode.OK, excluded.StatusCode);

        var first = await SyncAsync(app, listId);

        Assert.Equal(1, first.AddedCount);
        Assert.Equal(["Dune"], await MovieTitlesAsync(app));
        Assert.Contains("Arrival", await app.Client.GetStringAsync("/api/exclusions"));
        var exclusionId = (await app.Client.GetFromJsonAsync<JsonElement>("/api/exclusions")).EnumerateArray().Single().GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/exclusions/{exclusionId}")).StatusCode);

        var second = await SyncAsync(app, listId);

        Assert.Equal(1, second.AddedCount);
        Assert.Equal(["Arrival", "Dune"], await MovieTitlesAsync(app));
    }

    [Fact]
    public async Task Every_enabled_list_is_re_synced_when_its_interval_has_passed_one_job_each_and_a_paused_or_recently_synced_list_is_left_alone()
    {
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<IMetadataProvider>(Cards()));
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var due = await CreateListAsync(app, libraryId, "http://127.0.0.1:1/a.xml", new { syncIntervalHours = 1 }, "Due");
        var paused = await CreateListAsync(app, libraryId, "http://127.0.0.1:1/b.xml", new { syncIntervalHours = 1, isEnabled = false }, "Paused");
        var fresh = await CreateListAsync(app, libraryId, "http://127.0.0.1:1/c.xml", new { syncIntervalHours = 168 }, "Fresh");
        // Lists created two hours ago: the 1-hour lists are due, the 168-hour one is not.
        var twoHoursAgo = DateTimeOffset.UtcNow.AddHours(-2).ToString("O");
        await ApiScenes.ExecuteAsync(app, DelunoDatabaseNames.Platform, $"UPDATE intake_sources SET created_utc = '{twoHoursAgo}', last_sync_utc = NULL");
        var planner = app.Services.GetRequiredService<IntakeSyncService>();

        var queued = await planner.PlanDueSyncJobsAsync(CancellationToken.None);
        var queuedAgain = await planner.PlanDueSyncJobsAsync(CancellationToken.None);

        Assert.Equal(1, queued);
        Assert.Equal("1", await ApiScenes.TextAsync(app, DelunoDatabaseNames.Jobs, $"SELECT count(*) FROM job_queue WHERE job_type = 'intake.sync' AND related_entity_id = '{due}'"));
        Assert.Equal("0", await ApiScenes.TextAsync(app, DelunoDatabaseNames.Jobs, $"SELECT count(*) FROM job_queue WHERE related_entity_id IN ('{paused}','{fresh}')"));
        Assert.Equal("1", await ApiScenes.TextAsync(app, DelunoDatabaseNames.Jobs, $"SELECT count(*) FROM job_queue WHERE job_type = 'intake.sync' AND related_entity_id = '{due}'"));
        Assert.True(queuedAgain <= 1, "the same hour must not queue a second job for the same list");
    }

    [Fact]
    public async Task Creating_or_changing_a_list_refuses_a_bad_address_a_rating_outside_0_to_10_a_year_outside_1888_to_2100_an_age_of_zero_and_an_interval_outside_1_to_168_hours_and_stores_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var good = new Dictionary<string, object?> { ["name"] = "L", ["provider"] = "rss", ["feedUrl"] = "https://lists.example.test/a.xml", ["mediaType"] = "movies", ["libraryId"] = libraryId, ["searchOnAdd"] = false, ["isEnabled"] = true };

        foreach (var (field, bad, message) in new (string, object, string)[]
        {
            ("feedUrl", "not a url", "Use a public HTTP or HTTPS RSS/Atom feed URL."),
            ("minimumRating", 11.0, "Minimum rating must be between 0 and 10."),
            ("minimumYear", 1500, "Minimum year must be between 1888 and 2100."),
            ("maximumAgeDays", 0, "Maximum age in days must be greater than zero when provided."),
            ("syncIntervalHours", 500, "Sync interval must be between 1 and 168 hours.")
        })
        {
            var body = new Dictionary<string, object?>(good) { [field] = bad };
            var created = await app.Client.PostAsJsonAsync("/api/intake-sources", body);
            Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
            Assert.Contains(message, await created.Content.ReadAsStringAsync());
        }

        Assert.Equal("0", await StoredAsync(app, DelunoDatabaseNames.Platform, "SELECT count(*) FROM intake_sources"));
        var listId = await CreateListAsync(app, libraryId, "https://lists.example.test/a.xml");
        var changed = await app.Client.PutAsJsonAsync($"/api/intake-sources/{listId}", new Dictionary<string, object?>(good) { ["minimumRating"] = -1.0 });
        Assert.Equal(HttpStatusCode.BadRequest, changed.StatusCode);
        Assert.Contains("Minimum rating must be between 0 and 10.", await changed.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PutAsJsonAsync("/api/intake-sources/no-such-list", good)).StatusCode);
    }

    [Fact]
    public async Task An_rss_list_with_a_minimum_rating_judges_each_film_on_its_looked_up_rating_and_says_why_it_skips_one()
    {
        await using var feed = new FeedHolder(Feed(Item("Arrival (2016)"), Item("Gigli (2003)"), Item("Obscure Film (2011)")));
        var (server, url) = await ServeAsync(() => feed.Body);
        await using var _ = server;
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<IMetadataProvider>(
            Cards(Card("Arrival", 2016, "329865", 7.9), Card("Gigli", 2003, "11111", 2.4), Card("Obscure Film", 2011, "22222"))));
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var listId = await CreateListAsync(app, libraryId, url, new { minimumRating = 7.0 });

        var result = await SyncAsync(app, listId);

        Assert.Equal(["Arrival"], await MovieTitlesAsync(app));
        Assert.Equal(1, result.AddedCount);
        Assert.Equal(2, result.SkippedCount);
        Assert.Contains("Below minimum rating (7.0)", await app.Client.GetStringAsync("/api/activity?take=50"));
    }

    [Fact]
    public async Task A_plain_list_with_a_minimum_rating_judges_each_film_on_its_looked_up_rating_and_skips_one_with_no_rating_anywhere()
    {
        await using var feed = new FeedHolder("Arrival (2016)" + Environment.NewLine + "Gigli (2003)" + Environment.NewLine + "Obscure Film (2011)");
        var (server, url) = await ServeAsync(() => feed.Body);
        await using var _ = server;
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<IMetadataProvider>(
            Cards(Card("Arrival", 2016, "329865", 7.9), Card("Gigli", 2003, "11111", 2.4))));
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var listId = await CreateListAsync(app, libraryId, url, new { minimumRating = 7.0 });

        var result = await SyncAsync(app, listId);

        Assert.Equal(["Arrival"], await MovieTitlesAsync(app));
        Assert.Equal(1, result.AddedCount);
        Assert.Equal(2, result.SkippedCount);
        Assert.Contains("Below minimum rating (7.0)", await app.Client.GetStringAsync("/api/activity?take=50"));
    }

    private sealed class FeedHolder(string body) : IAsyncDisposable
    {
        public string Body { get; } = body;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
