using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Jobs;

public sealed class TheFullSearchRetryWindowListTests
{
    [Fact]
    public async Task The_list_returns_every_library_soonest_first_or_just_the_asked_one_and_honours_the_take()
    {
        using var storage = TestStorage.Create();
        var now = DateTimeOffset.Parse("2026-04-29T04:00:00Z");
        var time = new FixedTimeProvider(now);
        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, time),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var store = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());

        await store.RecordSearchRetryWindowAsync("movie", "m-late", "lib-a", "movies", "missing", now.AddHours(5), now, "Nothing found.", CancellationToken.None);
        await store.RecordSearchRetryWindowAsync("movie", "m-soon", "lib-a", "movies", "upgrade", now.AddHours(1), now, "Nothing better.", CancellationToken.None);
        await store.RecordSearchRetryWindowAsync("movie", "m-other", "lib-b", "movies", "missing", now.AddHours(3), now, null, CancellationToken.None);

        var all = await store.ListSearchRetryWindowsAsync(10, null, CancellationToken.None);
        Assert.Equal(["m-soon", "m-other", "m-late"], all.Select(i => i.EntityId));

        var blank = await store.ListSearchRetryWindowsAsync(10, "  ", CancellationToken.None);
        Assert.Equal(3, blank.Count);

        var libA = await store.ListSearchRetryWindowsAsync(10, "lib-a", CancellationToken.None);
        Assert.Equal(["m-soon", "m-late"], libA.Select(i => i.EntityId));
        Assert.Equal("Nothing better.", libA[0].LastResult);

        var capped = await store.ListSearchRetryWindowsAsync(1, null, CancellationToken.None);
        Assert.Equal("m-soon", Assert.Single(capped).EntityId);
    }
}
