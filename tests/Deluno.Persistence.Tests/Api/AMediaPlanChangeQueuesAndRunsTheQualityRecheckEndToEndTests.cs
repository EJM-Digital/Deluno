using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Worker.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// A library given another media plan (coverage register, engines): the whole application queues the quality recheck for that
/// library (the job type, its payload, the plain label), the worker's handler for that type runs it against the real catalogue, and
/// Activity gets one "Recalculated library quality" row naming the library and how many titles were rechecked. Films and shows.
/// </summary>
public sealed class AMediaPlanChangeQueuesAndRunsTheQualityRecheckEndToEndTests
{
    private static async Task<string> PlanAsync(ApplicationTestHost app, string mediaType)
    {
        var tiers = (await app.Client.GetFromJsonAsync<JsonElement>("/api/quality-model")).GetProperty("tiers")
            .EnumerateArray().Select(tier => tier.GetProperty("name").GetString()!).ToArray();
        using var profile = await app.Client.PostAsJsonAsync("/api/quality-profiles", new { name = "Profile " + mediaType, mediaType, cutoffQuality = tiers[0], allowedQualities = string.Join(",", tiers), customFormatIds = string.Empty, upgradeUntilCutoff = true });
        Assert.True(profile.IsSuccessStatusCode, await profile.Content.ReadAsStringAsync());
        var profileId = (await profile.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        using var plan = await app.Client.PostAsJsonAsync("/api/policy-sets", new { name = "Plan " + mediaType, mediaType, qualityProfileId = profileId, customFormatIds = string.Empty, upgradeUntilCutoff = true, isEnabled = true });
        Assert.True(plan.IsSuccessStatusCode, await plan.Content.ReadAsStringAsync());
        return (await plan.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task<JobQueueItem> QueuedAsync(ApplicationTestHost app, string jobType, string libraryId)
    {
        var page = await app.Services.GetRequiredService<IJobQueueRepository>()
            .ListPageAsync(new PageRequest(100, null), false, CancellationToken.None, true);
        return Assert.Single(page.Items, job => job.JobType == jobType && job.RelatedEntityId == libraryId);
    }

    private static async Task RunAsync(ApplicationTestHost app, JobQueueItem job)
    {
        var handler = app.Services.GetServices<IJobHandler>().Single(candidate => candidate.JobType == job.JobType);
        await handler.HandleAsync(job, CancellationToken.None);
    }

    [Fact]
    public async Task Giving_a_film_library_another_plan_queues_one_recheck_that_runs_and_writes_one_Activity_line()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(app, "Sintel", 2010);
        var plan = await PlanAsync(app, "movies");

        using var response = await app.Client.PutAsJsonAsync($"/api/libraries/{film.LibraryId}/media-plan", new { policySetId = plan });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        var job = await QueuedAsync(app, "movies.quality.recalculate", film.LibraryId);
        Assert.Equal("Recheck film quality", job.Label);
        Assert.Contains(film.LibraryId, job.PayloadJson);
        Assert.Contains(plan, job.PayloadJson);

        await RunAsync(app, job);
        var activity = await app.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(100, "library", film.LibraryId, CancellationToken.None);
        var line = Assert.Single(activity, item => item.Category == "library.quality.recalculated");
        Assert.Contains("1 movie record", line.Message);
        Assert.Equal("Recalculated library quality", ActivityEventLabels.For("library.quality.recalculated"));
    }

    [Fact]
    public async Task Giving_a_show_library_another_plan_queues_the_show_recheck_under_its_own_name()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var show = await ApiScenes.ShowOnDiskAsync(app, "Breaking Bad", 2008);
        var plan = await PlanAsync(app, "tv");

        using var response = await app.Client.PutAsJsonAsync($"/api/libraries/{show.LibraryId}/media-plan", new { policySetId = plan });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        var job = await QueuedAsync(app, "series.quality.recalculate", show.LibraryId);
        Assert.Equal("Recheck show quality", job.Label);

        await RunAsync(app, job);
        var activity = await app.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(100, "library", show.LibraryId, CancellationToken.None);
        Assert.Single(activity, item => item.Category == "library.quality.recalculated");
    }
}
