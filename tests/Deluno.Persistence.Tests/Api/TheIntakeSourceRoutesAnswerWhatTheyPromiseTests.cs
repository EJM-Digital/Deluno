using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Intake.Contracts;
using Deluno.Intake.Data;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The list routes other tools and the Lists screen use, on the real host: asking for a sync queues one intake job (and asking
/// twice does not queue a second), an unknown list is a 404; the diagnostics come a page at a time by pageSize and pageToken;
/// and the origins of a title are asked for by title and media type, with a plain 400 when they are not given.
/// </summary>
public sealed class TheIntakeSourceRoutesAnswerWhatTheyPromiseTests
{
    private static async Task<string> ListAsync(ApplicationTestHost app)
    {
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var response = await app.Client.PostAsJsonAsync("/api/intake-sources", new
        {
            name = "Weekend list", provider = "rss", feedUrl = "http://localhost:9/feed.xml", mediaType = "movies", libraryId,
            searchOnAdd = false, isEnabled = true, syncIntervalHours = 12
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Asking_for_a_sync_queues_one_intake_job_for_that_list_and_asking_again_does_not_queue_another()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var listId = await ListAsync(app);

        using var first = await app.Client.PostAsync($"/api/intake-sources/{listId}/sync", null);
        using var second = await app.Client.PostAsync($"/api/intake-sources/{listId}/sync", null);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        var job = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("intake.sync", job.GetProperty("jobType").GetString());
        Assert.Equal(listId, job.GetProperty("relatedEntityId").GetString());
        Assert.EndsWith($"/api/jobs/{job.GetProperty("id").GetString()}", first.Headers.Location!.ToString());
        var queued = (await app.Services.GetRequiredService<IJobQueueRepository>().ListAsync(100, CancellationToken.None))
            .Where(item => item.JobType == "intake.sync" && item.RelatedEntityId == listId && item.Status == "queued").ToArray();
        Assert.Single(queued);
    }

    [Fact]
    public async Task A_list_that_is_not_there_is_a_404_for_sync_and_for_diagnostics()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync("/api/intake-sources/no-such-list/sync", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/api/intake-sources/no-such-list/diagnostics")).StatusCode);
        Assert.DoesNotContain(await app.Services.GetRequiredService<IJobQueueRepository>().ListAsync(100, CancellationToken.None), item => item.JobType == "intake.sync");
    }

    [Fact]
    public async Task The_diagnostics_come_a_page_at_a_time_and_the_token_goes_on_from_where_the_page_stopped()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var listId = await ListAsync(app);
        var activity = app.Services.GetRequiredService<IActivityFeedRepository>();
        for (var number = 1; number <= 5; number++)
        {
            await activity.RecordActivityAsync("intake.synced", $"Line {number}", null, null, "intake-source", listId, CancellationToken.None);
        }

        var first = await app.Client.GetFromJsonAsync<JsonElement>($"/api/intake-sources/{listId}/diagnostics?pageSize=2");
        var firstPage = first.GetProperty("diagnostics");
        Assert.Equal(listId, first.GetProperty("source").GetProperty("id").GetString());
        Assert.Equal(2, firstPage.GetProperty("items").GetArrayLength());
        Assert.True(firstPage.GetProperty("hasMore").GetBoolean());
        var token = firstPage.GetProperty("nextPageToken").GetString()!;

        var second = (await app.Client.GetFromJsonAsync<JsonElement>($"/api/intake-sources/{listId}/diagnostics?pageSize=2&pageToken={Uri.EscapeDataString(token)}")).GetProperty("diagnostics");

        Assert.Equal(2, second.GetProperty("items").GetArrayLength());
        var seen = firstPage.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray()).Select(item => item.GetProperty("message").GetString()).ToArray();
        Assert.Equal(4, seen.Distinct().Count());
    }

    [Fact]
    public async Task The_origins_of_a_title_are_listed_by_title_and_media_type_and_a_request_without_them_is_a_400()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var listId = await ListAsync(app);
        var intake = app.Services.GetRequiredService<IIntakeRepository>();
        await intake.RecordIntakeTitleOriginAsync(
            new CreateIntakeTitleOriginRequest(listId, "Weekend list", "rss", "movies", "movie-1", "arrival-2016", "Arrival", 2016, "tt2543164"), CancellationToken.None);

        var found = await app.Client.GetFromJsonAsync<JsonElement>("/api/intake-title-origins?mediaType=movies&entityId=movie-1");
        var other = await app.Client.GetFromJsonAsync<JsonElement>("/api/intake-title-origins?mediaType=tv&entityId=movie-1");

        var origin = Assert.Single(found.EnumerateArray());
        Assert.Equal(listId, origin.GetProperty("sourceId").GetString());
        Assert.Equal("Arrival", origin.GetProperty("title").GetString());
        Assert.Equal(0, other.GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.GetAsync("/api/intake-title-origins?mediaType=movies")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.GetAsync("/api/intake-title-origins?mediaType=music&entityId=movie-1")).StatusCode);
    }
}
