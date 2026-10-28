using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// <c>POST /api/command</c>: the refresh commands with no id (every film, every show), a show by id, and a command's
/// status following the jobs behind it from queued to started to completed, or to failed when a job fails.
/// </summary>
public sealed class CommandEdgesTests
{
    [Fact]
    public async Task Refreshing_films_with_no_ids_marks_every_film_and_says_how_many()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        foreach (var (title, year) in new[] { ("Alien", 1979), ("Aliens", 1986), ("Alien 3", 1992) })
        {
            var added = await app.Client.PostAsJsonAsync("/api/movies", new { title, releaseYear = year, monitored = true });
            Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync());
        }

        var response = await app.Client.PostAsJsonAsync("/api/command", new { name = "RefreshMovie" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var command = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("completed", command.GetProperty("status").GetString());
        Assert.Equal("Marked 3 films for a details refresh.", command.GetProperty("message").GetString());
        Assert.Equal(0, command.GetProperty("jobIds").GetArrayLength());
    }

    [Fact]
    public async Task Refreshing_shows_with_no_ids_marks_every_show_a_show_by_id_queues_its_refresh_and_an_unknown_id_fails_the_command()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var created = await app.Client.PostAsJsonAsync("/api/series/", new { title = "Andor", startYear = 2022, monitored = true });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var seriesId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var all = await (await app.Client.PostAsJsonAsync("/api/command", new { name = "RefreshSeries" })).Content.ReadFromJsonAsync<JsonElement>();
        var one = await (await app.Client.PostAsJsonAsync("/api/command", new { name = "RefreshSeries", seriesIds = new[] { seriesId } })).Content.ReadFromJsonAsync<JsonElement>();
        var missing = await (await app.Client.PostAsJsonAsync("/api/command", new { name = "RefreshSeries", seriesId = "nope" })).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Marked 1 show for a details refresh.", all.GetProperty("message").GetString());
        Assert.Equal("Refreshing 1 show.", one.GetProperty("message").GetString());
        var jobId = Assert.Single(one.GetProperty("jobIds").EnumerateArray()).GetString()!;
        var job = await app.Services.GetRequiredService<IJobQueueRepository>().GetAsync(jobId, CancellationToken.None);
        Assert.Equal("series.metadata.refresh", job!.JobType);
        Assert.Equal(seriesId, job.RelatedEntityId);
        Assert.Equal("failed", missing.GetProperty("status").GetString());
        Assert.Contains("nope", missing.GetProperty("exception").GetString());
    }

    [Fact]
    public async Task A_commands_status_follows_its_job_from_queued_to_started_to_completed()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (commandId, jobId) = await QueueOneFeedSyncAsync(app);
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();

        Assert.Equal("queued", await StatusAsync(app, commandId));

        var leased = await jobs.LeaseNextAsync("test-worker", TimeSpan.FromMinutes(2), ["intake.sync"], CancellationToken.None);
        Assert.Equal(jobId, leased!.Id);
        Assert.Equal("started", await StatusAsync(app, commandId));

        await jobs.CompleteAsync(jobId, "test-worker", "Done.", CancellationToken.None);
        Assert.Equal("completed", await StatusAsync(app, commandId));
    }

    [Fact]
    public async Task A_failed_job_makes_its_command_failed_and_unsuccessful_with_the_jobs_error()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (commandId, jobId) = await QueueOneFeedSyncAsync(app);
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        await jobs.LeaseNextAsync("test-worker", TimeSpan.FromMinutes(2), ["intake.sync"], CancellationToken.None);

        // Fail it until the queue stops retrying it (a job is dead-lettered after its last attempt).
        for (var attempt = 0; attempt < 12; attempt++)
        {
            await jobs.FailAsync(jobId, "test-worker", "The feed would not load.", CancellationToken.None);
            var job = await jobs.GetAsync(jobId, CancellationToken.None);
            if (job!.Status is "failed" or "dead-letter") break;
            await jobs.LeaseNextAsync("test-worker", TimeSpan.FromMinutes(2), ["intake.sync"], CancellationToken.None);
        }

        var read = await app.Client.GetFromJsonAsync<JsonElement>($"/api/command/{commandId}");
        Assert.Equal("failed", read.GetProperty("status").GetString());
        Assert.Equal("unsuccessful", read.GetProperty("result").GetString());
        Assert.Contains("The feed would not load.", read.GetProperty("exception").GetString());
    }

    private static async Task<(string CommandId, string JobId)> QueueOneFeedSyncAsync(ApplicationTestHost app)
    {
        var source = await app.Client.PostAsJsonAsync("/api/intake-sources", new
        {
            name = "Watchlist", provider = "rss", feedUrl = "https://example.invalid/feed.xml", isEnabled = true, mediaType = "movies"
        });
        Assert.True(source.IsSuccessStatusCode, await source.Content.ReadAsStringAsync());
        var command = await (await app.Client.PostAsJsonAsync("/api/command", new { name = "RssSync" })).Content.ReadFromJsonAsync<JsonElement>();
        return (command.GetProperty("id").GetString()!, Assert.Single(command.GetProperty("jobIds").EnumerateArray()).GetString()!);
    }

    private static async Task<string?> StatusAsync(ApplicationTestHost app, string commandId)
        => (await app.Client.GetFromJsonAsync<JsonElement>($"/api/command/{commandId}")).GetProperty("status").GetString();
}
