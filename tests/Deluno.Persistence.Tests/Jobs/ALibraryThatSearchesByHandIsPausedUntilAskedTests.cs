using System.Text.Json;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Jobs;

/// <summary>
/// "Search automatically" off keeps a library to manual searches: the planner shows it as paused and queues nothing for it, however
/// many times it looks, until a person asks (search now), and that one request is searched whole. Turning only the missing search or
/// only the upgrade search off stops just that kind. Real planner, real SQLite job store.
/// </summary>
public sealed class ALibraryThatSearchesByHandIsPausedUntilAskedTests
{
    [Fact]
    public async Task A_library_with_search_automatically_off_is_paused_and_nothing_is_queued_for_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var library = Plan("library-by-hand", autoSearch: false, missing: true, upgrade: true);

        await jobs.PlanLibrarySearchesAsync([library], CancellationToken.None);
        await jobs.PlanLibrarySearchesAsync([library], CancellationToken.None);

        Assert.Equal("paused", (await jobs.ListLibraryAutomationStatesAsync(CancellationToken.None))[library.LibraryId].Status);
        Assert.Empty(await SearchJobsAsync(jobs));
    }

    [Fact]
    public async Task A_person_asking_for_a_search_now_gets_one_search_of_the_whole_library_even_with_search_automatically_off()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var library = Plan("library-by-hand", autoSearch: false, missing: true, upgrade: true);
        await jobs.PlanLibrarySearchesAsync([library], CancellationToken.None);

        Assert.True(await jobs.RequestLibrarySearchAsync(library, CancellationToken.None));
        await jobs.PlanLibrarySearchesAsync([library], CancellationToken.None);

        var queued = Assert.Single(await SearchJobsAsync(jobs));
        using var payload = JsonDocument.Parse(queued.PayloadJson!);
        Assert.True(payload.RootElement.GetProperty("checkMissing").GetBoolean());
        Assert.True(payload.RootElement.GetProperty("checkUpgrades").GetBoolean());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Switching_the_missing_or_the_upgrade_search_off_stops_only_that_kind(bool missing, bool upgrade)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var library = Plan("library-one-kind", autoSearch: true, missing: missing, upgrade: upgrade);

        await jobs.PlanLibrarySearchesAsync([library], CancellationToken.None);

        var queued = Assert.Single(await SearchJobsAsync(jobs));
        using var payload = JsonDocument.Parse(queued.PayloadJson!);
        Assert.Equal(missing, payload.RootElement.GetProperty("checkMissing").GetBoolean());
        Assert.Equal(upgrade, payload.RootElement.GetProperty("checkUpgrades").GetBoolean());
    }

    private static async Task<IReadOnlyList<JobQueueItem>> SearchJobsAsync(IJobQueueRepository jobs)
        => (await jobs.ListAsync(100, CancellationToken.None))
            .Where(job => job.JobType == LibrarySearchJobTypes.Movies || job.JobType == LibrarySearchJobTypes.Tv)
            .ToArray();

    private static LibraryAutomationPlanItem Plan(string id, bool autoSearch, bool missing, bool upgrade)
        => new(id, "Films", "movies", autoSearch, missing, upgrade, SearchIntervalMinutes: 360, RetryDelayHours: 24, MaxItemsPerRun: 25, SearchWindowStartHour: null, SearchWindowEndHour: null);
}
