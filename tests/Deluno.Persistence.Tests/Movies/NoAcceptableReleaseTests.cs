using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Data;
using Deluno.Media;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Movies;

/// <summary>
/// #1098: the bell lists a wanted title whose newest search found nothing Deluno would take, and only while that
/// stays true; a title the person dismissed stays out, and comes back as a new row if it becomes the problem again.
/// </summary>
public sealed class NoAcceptableReleaseTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-01T09:00:00Z");

    private static async Task<(TestStorage Storage, SqliteMovieCatalogRepository Movies)> CreateAsync()
    {
        var storage = TestStorage.Create();
        var time = new FixedTimeProvider(Now);
        await new MoviesSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, time), NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new JobsSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, time), NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new SeriesSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, time), NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return (storage, new SqliteMovieCatalogRepository(storage.Factory, time));
    }

    private static async Task<string> WantedMovieAsync(SqliteMovieCatalogRepository movies, string title, string outcome, DateTimeOffset searchedUtc, string status = "missing")
    {
        var added = await movies.AddAsync(new CreateMovieRequest(title, 2020, null), CancellationToken.None);
        await movies.EnsureWantedStateAsync(added.Id, "lib", status, "Wanted", hasFile: false, null, "WEB 1080p", false, CancellationToken.None);
        await movies.RecordSearchAttemptAsync(added.Id, "lib", "automatic", outcome, searchedUtc, null, "Searched and found nothing.", null, null, null, CancellationToken.None);
        return added.Id;
    }

    [Fact]
    public async Task A_missing_title_whose_newest_search_found_nothing_is_listed_and_others_are_not()
    {
        var (storage, movies) = await CreateAsync();
        using var _ = storage;
        var nothing = await WantedMovieAsync(movies, "Nothing Found", "checked", Now.AddHours(-2));
        await WantedMovieAsync(movies, "Grabbed", "matched", Now.AddHours(-2));
        await WantedMovieAsync(movies, "Not Out Yet", "checked", Now.AddHours(-2), status: "upcoming");

        var listed = await new SqliteNoAcceptableReleaseReader(storage.Factory).ListAsync(50, CancellationToken.None);

        var item = Assert.Single(listed);
        Assert.Equal(nothing, item.Id);
        Assert.Equal("movie", item.MediaType);
        Assert.Equal("Nothing Found", item.Title);
        Assert.Equal("lib", item.LibraryId);
    }

    [Fact]
    public async Task A_later_search_that_found_something_takes_the_title_off_the_list()
    {
        var (storage, movies) = await CreateAsync();
        using var _ = storage;
        var id = await WantedMovieAsync(movies, "Found Later", "checked", Now.AddHours(-5));
        Assert.Single(await new SqliteNoAcceptableReleaseReader(storage.Factory).ListAsync(50, CancellationToken.None));

        await movies.RecordSearchAttemptAsync(id, "lib", "automatic", "matched", Now.AddHours(-1), null, "Sent.", null, null, null, CancellationToken.None);

        Assert.Empty(await new SqliteNoAcceptableReleaseReader(storage.Factory).ListAsync(50, CancellationToken.None));
    }

    [Fact]
    public async Task A_dismissal_is_kept_while_it_is_the_problem_and_forgotten_when_it_stops_being_one()
    {
        var (storage, _) = await CreateAsync();
        using var _ = storage;
        var store = new AttentionDismissalStore(storage.Factory, new FixedTimeProvider(Now));
        var key = AttentionDismissalStore.NoReleaseKey("movie", "m1");

        await store.DismissAsync(key, CancellationToken.None);
        await store.DismissAsync(key, CancellationToken.None);
        Assert.Equal([key], (await store.ListAsync(CancellationToken.None)).ToArray());

        await store.ForgetExceptAsync(new HashSet<string> { key }, CancellationToken.None);
        Assert.Contains(key, await store.ListAsync(CancellationToken.None));

        await store.ForgetExceptAsync(new HashSet<string>(), CancellationToken.None);
        Assert.Empty(await store.ListAsync(CancellationToken.None));
    }
}
