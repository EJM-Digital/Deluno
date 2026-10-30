using System.Text.Json;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Jobs;

/// <summary>
/// Coverage register (#1329): the catalog-refresh handlers queue the add's own search with a request that is the same every time
/// for the same add (no key of its own), so the real job store's default de-duplication merges a repeated refresh into one queued
/// search. A different title is a different search.
/// </summary>
public sealed class ARepeatedAddQueuesOneSearchTests
{
    private static EnqueueJobRequest Search(string mediaType, string target) => new(
        JobType: LibrarySearchJobTypes.For(mediaType),
        Source: mediaType == "movies" ? "movies" : "series",
        PayloadJson: JsonSerializer.Serialize(new
        {
            libraryId = "library-1",
            libraryName = "Library",
            mediaType,
            checkMissing = true,
            checkUpgrades = true,
            maxItems = 1,
            retryDelayHours = 6,
            triggeredBy = "added",
            targetEntityId = target
        }),
        RelatedEntityType: mediaType == "movies" ? "movie" : "series",
        RelatedEntityId: target);

    [Theory]
    [InlineData("movies")]
    [InlineData("tv")]
    public async Task The_same_add_refreshed_twice_leaves_one_queued_search_and_another_title_gets_its_own(string mediaType)
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-01T06:00:00Z"));
        await new JobsSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, time), NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var jobs = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());

        var first = await jobs.EnqueueAsync(Search(mediaType, "title-1"), CancellationToken.None);
        var second = await jobs.EnqueueAsync(Search(mediaType, "title-1"), CancellationToken.None);
        var other = await jobs.EnqueueAsync(Search(mediaType, "title-2"), CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.NotEqual(first.Id, other.Id);
        var count = await CountJobs(storage, LibrarySearchJobTypes.For(mediaType));
        Assert.Equal("2", count);
    }

    private static async Task<string?> CountJobs(TestStorage storage, string jobType)
    {
        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM job_queue WHERE job_type = @type;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@type";
        parameter.Value = jobType;
        command.Parameters.Add(parameter);
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
