using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Realtime;

/// <summary>
/// #1491: the Dashboard's Activity panel opens on the newest page of the history. Two hundred routine rows written
/// after five strikes filled the page and the panel opened empty of them. Asking with the routine categories left out
/// puts the strikes on the first page, and asking without leaves the list as it was.
/// </summary>
public sealed class TheActivityPanelsFirstPageIsNotBuriedByRoutineRowsTests : IDisposable
{
    private readonly TestStorage _storage = TestStorage.Create();
    private readonly FixedTimeProvider _time = new(DateTimeOffset.Parse("2026-11-05T04:30:00Z"));
    private readonly SqliteJobStore _jobs;

    public TheActivityPanelsFirstPageIsNotBuriedByRoutineRowsTests()
    {
        new JobsSchemaInitializer(_storage.Factory, new SqliteDatabaseMigrator(_storage.Factory, _time), NullLogger<JobsSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        _jobs = new SqliteJobStore(_storage.Factory, _time, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
    }

    public void Dispose() => _storage.Dispose();

    private async Task WriteAsync()
    {
        for (var i = 0; i < 5; i++)
        {
            await _jobs.RecordActivityAsync("download.health.remediated", $"Strike {i}", null, null, "movie", $"m{i}", CancellationToken.None);
        }

        for (var i = 0; i < 200; i++)
        {
            await _jobs.RecordActivityAsync(i % 2 == 0 ? "job.started" : "job.completed", $"Task {i}", null, null, null, null, CancellationToken.None);
        }
    }

    [Fact]
    public async Task With_the_routine_categories_left_out_the_first_page_holds_the_five_strikes()
    {
        await WriteAsync();

        var page = await _jobs.ListActivityPageAsync(
            new PageRequest(40, null),
            new ActivityQuery(ExcludeCategories: ["job.started", "job.completed", "job.queued"]),
            CancellationToken.None);

        Assert.Equal(5, page.Items.Count);
        Assert.All(page.Items, item => Assert.Equal("download.health.remediated", item.Category));
    }

    [Fact]
    public async Task Without_the_filter_the_list_is_what_it_was()
    {
        await WriteAsync();

        var page = await _jobs.ListActivityPageAsync(new PageRequest(40, null), new ActivityQuery(), CancellationToken.None);

        Assert.Equal(40, page.Items.Count);
        Assert.DoesNotContain(page.Items, item => item.Category == "download.health.remediated");
    }
}
