using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Worker.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Searching a chosen set of films or shows at once: the chosen titles are found by id, the libraries they are wanted in each
/// get one search, a choice of nothing is refused in plain words, and the per-title "search" operation of the bulk editor queues
/// one search job per title. Real host and databases.
/// </summary>
public sealed class BulkSearchTests
{
    [Fact]
    public async Task Chosen_films_in_two_libraries_ask_one_search_in_each_and_say_how_many()
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
        Assert.Equal(2, body.GetProperty("searchesTriggered").GetInt32());
        Assert.Equal(2, body.GetProperty("libraryCount").GetInt32());
        var asked = (await app.Services.GetRequiredService<IJobQueueRepository>().ListLibraryAutomationStatesAsync(CancellationToken.None))
            .Values.Where(state => state.SearchRequested).Select(state => state.LibraryId).Order().ToArray();
        Assert.Equal(new[] { first, second }.Order().ToArray(), asked);
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
        Assert.DoesNotContain(
            (await app.Services.GetRequiredService<IJobQueueRepository>().ListLibraryAutomationStatesAsync(CancellationToken.None)).Values,
            state => state.SearchRequested);
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

    [Fact(Skip = "DEFECT: POST /api/movies/bulk with operation \"search\" answers success and queues a 'movies.search.manual' job, but no job handler is registered for that job type (JobHandlerRegistry.RegisteredJobTypes has none), so the search is never run. 'series.search.manual' (POST /api/series/bulk) is the same.")]
    public async Task Every_search_job_the_bulk_editor_queues_has_a_handler_that_runs_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var registry = new JobHandlerRegistry(app.Services.GetServices<IJobHandler>());

        Assert.Contains("movies.search.manual", registry.RegisteredJobTypes);
        Assert.Contains("series.search.manual", registry.RegisteredJobTypes);
        await Task.CompletedTask;
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
