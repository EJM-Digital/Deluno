using System.Net;
using System.Net.Http.Json;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// "Search now" and "Skip this cycle" on a library (coverage register, area api): what each leaves in the job queue file, the library's
/// own automation state and Activity, and that another library's work is not touched.
/// </summary>
public sealed class ALibrarysSearchCycleCanBeRequestedOrSkippedTests
{
    private static async Task<string> AutoLibraryAsync(ApplicationTestHost app, string folder, string name)
    {
        var id = await ApiScenes.CreateLibraryAsync(app, "movies", folder, name);
        var saved = await app.Client.PutAsJsonAsync($"/api/libraries/{id}/automation", new
        {
            autoSearchEnabled = true, missingSearchEnabled = true, upgradeSearchEnabled = true, searchIntervalMinutes = 60, retryDelayHours = 6, maxItemsPerRun = 5
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        return id;
    }

    [Fact]
    public async Task Search_now_marks_the_library_as_asked_for_and_says_so_in_Activity_and_an_unknown_library_is_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await AutoLibraryAsync(app, "Films", "Films");
        Assert.Equal(0, await ApiScenes.ScalarAsync(app, "jobs", $"SELECT COUNT(*) FROM library_automation_state WHERE library_id = '{id}' AND search_requested = 1"));

        var response = await app.Client.PostAsync($"/api/libraries/{id}/search-now", content: null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(1, await ApiScenes.ScalarAsync(app, "jobs", $"SELECT COUNT(*) FROM library_automation_state WHERE library_id = '{id}' AND search_requested = 1"));
        Assert.Equal("requested", await ApiScenes.TextAsync(app, "jobs", $"SELECT status FROM library_automation_state WHERE library_id = '{id}'"));
        Assert.Contains("Deluno will check Films on the next pass.", await app.Client.GetStringAsync("/api/activity?take=50"));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync("/api/libraries/no-such-library/search-now", content: null)).StatusCode);
    }

    [Fact]
    public async Task Skipping_a_cycle_clears_the_ask_removes_that_librarys_queued_search_sets_the_next_search_an_interval_ahead_and_leaves_another_librarys_search_queued()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var films = await AutoLibraryAsync(app, "Films", "Films");
        var other = await AutoLibraryAsync(app, "Films 4K", "Films 4K");
        var scheduler = app.Services.GetRequiredService<IJobScheduler>();
        foreach (var library in new[] { films, other })
        {
            await scheduler.EnqueueAsync(new EnqueueJobRequest(JobType: "library.search.movies", Source: "movies", PayloadJson: "{}", RelatedEntityType: "library", RelatedEntityId: library), CancellationToken.None);
        }

        Assert.Equal(HttpStatusCode.Accepted, (await app.Client.PostAsync($"/api/libraries/{films}/search-now", content: null)).StatusCode);
        var before = DateTimeOffset.UtcNow;

        var skipped = await app.Client.PostAsync($"/api/libraries/{films}/skip-cycle", content: null);

        Assert.Equal(HttpStatusCode.Accepted, skipped.StatusCode);
        Assert.Equal(0, await ApiScenes.ScalarAsync(app, "jobs", $"SELECT COUNT(*) FROM library_automation_state WHERE library_id = '{films}' AND search_requested = 1"));
        Assert.Equal("idle", await ApiScenes.TextAsync(app, "jobs", $"SELECT status FROM library_automation_state WHERE library_id = '{films}'"));
        Assert.Equal(0, await ApiScenes.ScalarAsync(app, "jobs", $"SELECT COUNT(*) FROM job_queue WHERE related_entity_id = '{films}' AND status = 'queued' AND job_type = 'library.search.movies'"));
        Assert.Equal(1, await ApiScenes.ScalarAsync(app, "jobs", $"SELECT COUNT(*) FROM job_queue WHERE related_entity_id = '{other}' AND status = 'queued' AND job_type = 'library.search.movies'"));
        var next = DateTimeOffset.Parse((await ApiScenes.TextAsync(app, "jobs", $"SELECT next_search_utc FROM library_automation_state WHERE library_id = '{films}'"))!);
        Assert.InRange(next, before.AddMinutes(59), before.AddMinutes(61).AddSeconds(30));
        Assert.Contains("Skipped the current cycle for Films; removed 1 queued search job.", await app.Client.GetStringAsync("/api/activity?take=50"));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync("/api/libraries/no-such-library/skip-cycle", content: null)).StatusCode);
    }
}
