using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Worker.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Searching a chosen set of films or shows at once: the chosen titles are found by id, each chosen title is searched (and no
/// other title of its library), a choice of nothing is refused in plain words, and the per-title "search" operation of the
/// bulk editor queues one search job per title. Real host and databases.
/// </summary>
public sealed class BulkSearchTests
{
    [Fact]
    public async Task Chosen_films_in_two_libraries_each_get_their_own_search_and_no_library_is_flagged()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var first = await LibraryAsync(app, "Films", "movies");
        var second = await LibraryAsync(app, "More films", "movies");
        var inFirst = await FilmAsync(app, "Chosen One", 2020, first);
        var alsoInFirst = await FilmAsync(app, "Chosen Two", 2021, first);
        var inSecond = await FilmAsync(app, "Chosen Three", 2022, second);

        using var response = await app.Client.PostAsJsonAsync("/api/movies/bulk/search", new { movieIds = new[] { inFirst, alsoInFirst, inSecond } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, body.GetProperty("searchesTriggered").GetInt32());
        Assert.Equal(2, body.GetProperty("libraryCount").GetInt32());
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var queued = (await jobs.ListAsync(100, CancellationToken.None)).Where(job => job.JobType == "movies.search.manual").ToArray();
        Assert.Equal(new[] { inFirst, alsoInFirst, inSecond }.Order().ToArray(), queued.Select(job => job.RelatedEntityId!).Order().ToArray());
        // Nothing flags a whole library for a search: only the chosen titles are searched.
        Assert.DoesNotContain((await jobs.ListLibraryAutomationStatesAsync(CancellationToken.None)).Values, state => state.SearchRequested);
    }

    [Fact]
    public async Task Choosing_nothing_is_refused_and_a_title_that_is_wanted_nowhere_triggers_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await LibraryAsync(app, "Films", "movies");

        using var none = await app.Client.PostAsJsonAsync("/api/movies/bulk/search", new { movieIds = Array.Empty<string>() });
        using var unknown = await app.Client.PostAsJsonAsync("/api/movies/bulk/search", new { movieIds = new[] { "no-such-film" } });
        using var shows = await app.Client.PostAsJsonAsync("/api/series/bulk/search", new { seriesIds = Array.Empty<string>() });

        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Contains("Choose at least one movie to search for.", await none.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, shows.StatusCode);
        Assert.Contains("Choose at least one series to search for.", await shows.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        Assert.Equal(0, (await unknown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("searchesTriggered").GetInt32());
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        Assert.DoesNotContain(await jobs.ListAsync(100, CancellationToken.None), job => job.JobType == "movies.search.manual");
        Assert.DoesNotContain((await jobs.ListLibraryAutomationStatesAsync(CancellationToken.None)).Values, state => state.SearchRequested);
    }

    [Fact]
    public async Task Bulk_search_needs_a_sign_in()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var anonymous = app.CreateAnonymousClient();

        using var response = await anonymous.PostAsJsonAsync("/api/movies/bulk/search", new { movieIds = new[] { "x" } });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_bulk_editors_search_operation_queues_one_search_job_per_film_and_reports_each_job_id()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var library = await LibraryAsync(app, "Films", "movies");
        var one = await FilmAsync(app, "Editor One", 2020, library);
        var two = await FilmAsync(app, "Editor Two", 2021, library);

        using var response = await app.Client.PostAsJsonAsync("/api/movies/bulk", new { movieIds = new[] { one, two }, operation = "search" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("successCount").GetInt32());
        var jobIds = body.GetProperty("results").EnumerateArray().Select(item => item.GetProperty("metadata").GetProperty("jobId").GetString()!).ToArray();
        Assert.Equal(2, jobIds.Distinct().Count());
        var queued = await app.Services.GetRequiredService<IJobQueueRepository>().ListAsync(100, CancellationToken.None);
        Assert.All(jobIds, jobId => Assert.Equal("movies.search.manual", queued.Single(job => job.Id == jobId).JobType));
    }

    [Fact]
    public async Task The_bulk_editors_search_operation_queues_one_search_job_per_show_and_reports_each_job_id()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await LibraryAsync(app, "Shows", "tv");
        var ids = new List<string>();
        foreach (var title in new[] { "Editor Show One", "Editor Show Two" })
        {
            using var added = await app.Client.PostAsJsonAsync("/api/series", new { title, startYear = 2020, monitored = true });
            Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync());
            ids.Add((await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);
        }

        using var response = await app.Client.PostAsJsonAsync("/api/series/bulk", new { seriesIds = ids, operation = "search" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("successCount").GetInt32());
        var jobIds = body.GetProperty("results").EnumerateArray().Select(item => item.GetProperty("metadata").GetProperty("jobId").GetString()!).ToArray();
        var queued = await app.Services.GetRequiredService<IJobQueueRepository>().ListAsync(100, CancellationToken.None);
        Assert.All(jobIds, jobId => Assert.Equal("series.search.manual", queued.Single(job => job.Id == jobId).JobType));
    }

    [Fact]
    public async Task Every_search_job_the_bulk_editor_queues_has_a_handler_that_runs_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var registry = new JobHandlerRegistry(app.Services.GetServices<IJobHandler>());

        Assert.Contains("movies.search.manual", registry.RegisteredJobTypes);
        Assert.Contains("series.search.manual", registry.RegisteredJobTypes);
        Assert.Contains(Deluno.Worker.Services.JobLanes.All, lane => lane.JobTypes.Contains("movies.search.manual"));
        Assert.Contains(Deluno.Worker.Services.JobLanes.All, lane => lane.JobTypes.Contains("series.search.manual"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_search_that_runs_after_a_bulk_search_is_about_the_chosen_titles_and_not_the_whole_library()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var library = await LibraryAsync(app, "Films", "movies");
        var chosen = await FilmAsync(app, "Chosen Only", 2020, library);
        var other = await FilmAsync(app, "Not Chosen", 2021, library);
        var another = await FilmAsync(app, "Also Not Chosen", 2022, library);
        using var asked = await app.Client.PostAsJsonAsync("/api/movies/bulk/search", new { movieIds = new[] { chosen } });
        Assert.Equal(HttpStatusCode.OK, asked.StatusCode);
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();

        // Exactly one search is queued, for the chosen film in its library, and it says what started it.
        var queued = Assert.Single(await jobs.ListAsync(100, CancellationToken.None), item => item.JobType == "movies.search.manual");
        Assert.Equal(chosen, queued.RelatedEntityId);
        Assert.Contains(library, queued.PayloadJson);
        Assert.DoesNotContain(other, queued.PayloadJson);
        Assert.DoesNotContain(another, queued.PayloadJson);

        // The library cycle finds nothing flagged, so it queues no search of the whole library.
        Assert.DoesNotContain((await jobs.ListLibraryAutomationStatesAsync(CancellationToken.None)).Values, state => state.SearchRequested);
        var plan = new LibraryAutomationPlanItem(library, "Films", "movies", false, true, true, 360, 24, 25, null, null);
        await jobs.PlanLibrarySearchesAsync([plan], CancellationToken.None);
        Assert.DoesNotContain(await jobs.ListAsync(100, CancellationToken.None), item => item.JobType == Deluno.Contracts.LibrarySearchJobTypes.Movies);

        // Running it searches the chosen film and no other, and Activity says what started it.
        var registry = new JobHandlerRegistry(app.Services.GetServices<IJobHandler>());
        await registry.Resolve(queued.JobType).HandleAsync(queued, CancellationToken.None);
        var activity = app.Services.GetRequiredService<IActivityFeedRepository>();
        var line = Assert.Single(await activity.ListActivityAsync(100, "movie", chosen, CancellationToken.None), item => item.Category == "movie.search.bulk");
        Assert.Contains("Chosen Only", line.Message);
        Assert.Contains("started from the Search button on the library", line.Message);
        Assert.DoesNotContain(await activity.ListActivityAsync(100, "movie", other, CancellationToken.None), item => item.Category == "movie.search.bulk");
        Assert.DoesNotContain(await activity.ListActivityAsync(100, "movie", another, CancellationToken.None), item => item.Category == "movie.search.bulk");
    }

    [Fact]
    public async Task Chosen_shows_are_searched_one_by_one_and_each_search_says_the_bulk_search_started_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var library = await LibraryAsync(app, "Shows", "tv");
        var ids = new List<string>();
        foreach (var title in new[] { "Chosen Show", "Left Alone Show" })
        {
            using var added = await app.Client.PostAsJsonAsync("/api/series", new { title, startYear = 2020, monitored = true, libraryId = library });
            Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync());
            ids.Add((await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);
        }

        using var asked = await app.Client.PostAsJsonAsync("/api/series/bulk/search", new { seriesIds = new[] { ids[0] } });

        Assert.Equal(HttpStatusCode.OK, asked.StatusCode);
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var queued = Assert.Single(await jobs.ListAsync(100, CancellationToken.None), item => item.JobType == "series.search.manual");
        Assert.Equal(ids[0], queued.RelatedEntityId);
        Assert.DoesNotContain((await jobs.ListLibraryAutomationStatesAsync(CancellationToken.None)).Values, state => state.SearchRequested);
        var registry = new JobHandlerRegistry(app.Services.GetServices<IJobHandler>());
        await registry.Resolve(queued.JobType).HandleAsync(queued, CancellationToken.None);
        var activity = app.Services.GetRequiredService<IActivityFeedRepository>();
        var line = Assert.Single(await activity.ListActivityAsync(100, "series", ids[0], CancellationToken.None), item => item.Category == "series.search.bulk");
        Assert.Contains("Chosen Show", line.Message);
        Assert.Contains("started from the Search button on the library", line.Message);
        Assert.DoesNotContain(await activity.ListActivityAsync(100, "series", ids[1], CancellationToken.None), item => item.Category == "series.search.bulk");
    }

    [Fact]
    public async Task A_search_the_bulk_editor_queued_says_it_came_from_the_bulk_editor()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var library = await LibraryAsync(app, "Films", "movies");
        var film = await FilmAsync(app, "Editor Film", 2020, library);
        using var asked = await app.Client.PostAsJsonAsync("/api/movies/bulk", new { movieIds = new[] { film }, operation = "search" });
        Assert.Equal(HttpStatusCode.OK, asked.StatusCode);
        var queued = Assert.Single(await app.Services.GetRequiredService<IJobQueueRepository>().ListAsync(100, CancellationToken.None), item => item.JobType == "movies.search.manual");

        await new JobHandlerRegistry(app.Services.GetServices<IJobHandler>()).Resolve(queued.JobType).HandleAsync(queued, CancellationToken.None);

        var line = Assert.Single(await app.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(100, "movie", film, CancellationToken.None), item => item.Category == "movie.search.bulk");
        Assert.Contains("started from the bulk editor", line.Message);
    }

    private static async Task<string> LibraryAsync(ApplicationTestHost app, string name, string mediaType)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, name.Replace(' ', '-'))).FullName;
        using var response = await app.Client.PostAsJsonAsync("/api/libraries/", new { name, mediaType, rootPath = root });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task<string> FilmAsync(ApplicationTestHost app, string title, int year, string libraryId)
    {
        using var response = await app.Client.PostAsJsonAsync("/api/movies", new { title, releaseYear = year, monitored = true, libraryId });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }
}
