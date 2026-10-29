using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Removing a film through the bulk editor cancels the jobs still waiting for it and says how many it cancelled
/// (cancelledPendingJobCount), and leaves the jobs waiting for another film alone.
/// </summary>
public sealed class RemovingATitleCancelsItsPendingJobsAndSaysHowManyTests
{
    [Fact]
    public async Task The_jobs_waiting_for_the_removed_film_are_cancelled_and_counted_and_another_films_job_stays()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var gone = await ApiScenes.FilmOnDiskAsync(app, "Removed Film", 2001, libraryId);
        var stays = await ApiScenes.FilmOnDiskAsync(app, "Staying Film", 2002, libraryId);
        var scheduler = app.Services.GetRequiredService<IJobScheduler>();
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        async Task<JobQueueItem> WaitingFor(string movieId, int number)
            => await scheduler.EnqueueAsync(
                new EnqueueJobRequest("movies.metadata.refresh", "test", "{\"n\":" + number + "}", "movie", movieId, DedupeKey: $"row:{movieId}:{number}"),
                CancellationToken.None);
        var first = await WaitingFor(gone.MovieId, 1);
        var second = await WaitingFor(gone.MovieId, 2);
        var other = await WaitingFor(stays.MovieId, 1);

        using var response = await app.Client.PostAsJsonAsync("/api/movies/bulk", new { movieIds = new[] { gone.MovieId }, operation = "remove", deleteFiles = false });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var result = Assert.Single(body.GetProperty("results").EnumerateArray());
        Assert.True(result.GetProperty("succeeded").GetBoolean());
        Assert.Equal("2", result.GetProperty("metadata").GetProperty("cancelledPendingJobCount").GetString());
        Assert.NotEqual("queued", (await jobs.GetAsync(first.Id, CancellationToken.None))?.Status);
        Assert.NotEqual("queued", (await jobs.GetAsync(second.Id, CancellationToken.None))?.Status);
        Assert.Equal("queued", (await jobs.GetAsync(other.Id, CancellationToken.None))?.Status);
    }
}
