using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Jobs;

/// <summary>
/// #1062: the remove guard finds Deluno's own dispatch by the client's id
/// whatever case the client reports it in - the same as the Queue screen - so
/// a hash stored in capitals and reported in lower case is not "not sent by
/// Deluno".
/// </summary>
public sealed class TheRemoveGuardMatchesIdsIgnoringCaseTests
{
    [Fact]
    public async Task A_hash_is_found_whatever_its_case()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-30T12:00:00Z"));
        var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
        await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, time);
        var jobs = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), dispatches);
        var id = await jobs.RecordDownloadDispatchAsync(
            "library-1", "movies", "movie", "movie-1", "Some.Movie.2026.1080p.WEB", "Fixture", "client-1", "Client", "sent", null,
            cancellationToken: CancellationToken.None);
        await dispatches.RecordDetectionAsync(id, "ABCDEF0123", 1, completed: false, CancellationToken.None);

        Assert.NotNull(await dispatches.FindAnyDispatchByHashAsync("client-1", "abcdef0123", CancellationToken.None));
        Assert.NotNull(await dispatches.FindDispatchByHashAsync("client-1", "abcdef0123", CancellationToken.None));
        await dispatches.ArchiveDispatchAsync(id, "done", CancellationToken.None);
        Assert.NotNull(await dispatches.FindAnyDispatchByHashAsync("client-1", "AbCdEf0123", CancellationToken.None));
        Assert.Null(await dispatches.FindAnyDispatchByHashAsync("client-2", "abcdef0123", CancellationToken.None));
    }
}
