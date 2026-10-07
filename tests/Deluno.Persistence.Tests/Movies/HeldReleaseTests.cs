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
/// #1159: a search that ended "held" leaves a usable release waiting for a person's yes. Transfers lists it under
/// Needs action with Approve / Find another / Dismiss, and it stops being listed once answered or once the title has
/// its file. Approving needs the release's link, so the stored plan keeps it for the held candidate.
/// </summary>
public sealed class HeldReleaseTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-08T09:00:00Z");

    private const string HeldPlan =
        """{"bestCandidate":{"releaseName":"Big.Buck.Bunny.2008.2160p.WEB-DL-GRP","indexerName":"Nyaa","indexerId":"ix1","quality":"WEB 2160p","sizeBytes":8000000000,"seeders":12,"downloadUrl":"https://indexer.example/get/1","decisionStatus":"held","summary":"Held for review by the typed release plan."},"candidates":[{"releaseName":"Big.Buck.Bunny.2008.2160p.WEB-DL-GRP","decisionStatus":"held","downloadUrl":"https://indexer.example/get/1"}]}""";

    private static async Task<(TestStorage Storage, SqliteMovieCatalogRepository Movies)> CreateAsync()
    {
        var storage = TestStorage.Create();
        var time = new FixedTimeProvider(Now);
        await new MoviesSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, time), NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new JobsSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, time), NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new SeriesSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, time), NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return (storage, new SqliteMovieCatalogRepository(storage.Factory, time));
    }

    private static async Task<string> HeldMovieAsync(SqliteMovieCatalogRepository movies, string title, string outcome = "held", string? plan = HeldPlan)
    {
        var added = await movies.AddAsync(new CreateMovieRequest(title, 2008, null), CancellationToken.None);
        await movies.EnsureWantedStateAsync(added.Id, "lib", "missing", "Wanted", hasFile: false, null, "WEB 1080p", false, CancellationToken.None);
        await movies.RecordSearchAttemptAsync(added.Id, "lib", "automatic", outcome, Now.AddHours(-1), null, "Held.", "Big.Buck.Bunny.2008.2160p.WEB-DL-GRP", "Nyaa", plan, CancellationToken.None);
        return added.Id;
    }

    [Fact]
    public async Task A_held_search_lists_its_candidate_with_the_link_the_approval_will_send()
    {
        var (storage, movies) = await CreateAsync();
        using var _ = storage;
        var id = await HeldMovieAsync(movies, "Big Buck Bunny");
        await HeldMovieAsync(movies, "Grabbed", outcome: "matched");

        var item = Assert.Single(await new SqliteHeldReleaseReader(storage.Factory).ListAsync(50, CancellationToken.None));

        Assert.Equal(id, item.Id);
        Assert.Equal("movie", item.MediaType);
        Assert.Equal("Big.Buck.Bunny.2008.2160p.WEB-DL-GRP", item.ReleaseName);
        Assert.Equal("https://indexer.example/get/1", item.DownloadUrl);
        Assert.Equal("ix1", item.IndexerId);
        Assert.Equal("WEB 2160p", item.Quality);
    }

    [Fact]
    public async Task A_refused_candidate_is_never_listed_as_waiting_for_approval()
    {
        var (storage, movies) = await CreateAsync();
        using var _ = storage;
        await HeldMovieAsync(movies, "Wrong Show", plan: HeldPlan.Replace("\"decisionStatus\":\"held\",\"summary\"", "\"decisionStatus\":\"rejected\",\"summary\""));

        Assert.Empty(await new SqliteHeldReleaseReader(storage.Factory).ListAsync(50, CancellationToken.None));
    }

    [Fact]
    public async Task A_later_search_that_sent_something_takes_it_off_the_list()
    {
        var (storage, movies) = await CreateAsync();
        using var _ = storage;
        var id = await HeldMovieAsync(movies, "Big Buck Bunny");
        Assert.Single(await new SqliteHeldReleaseReader(storage.Factory).ListAsync(50, CancellationToken.None));

        await movies.RecordSearchAttemptAsync(id, "lib", "automatic", "matched", Now, null, "Sent.", null, null, null, CancellationToken.None);

        Assert.Empty(await new SqliteHeldReleaseReader(storage.Factory).ListAsync(50, CancellationToken.None));
    }

    [Fact]
    public async Task The_stored_plan_keeps_the_held_candidates_link_when_a_search_with_many_releases_is_trimmed()
    {
        var many = string.Join(",", Enumerable.Range(0, 20).Select(index => $$"""{"releaseName":"R{{index}}","decisionStatus":"held","downloadUrl":"https://indexer.example/get/{{index}}"}"""));
        var plan = """{"bestCandidate":{"releaseName":"Best","indexerName":"Nyaa","decisionStatus":"held","downloadUrl":"https://indexer.example/get/best"},"candidates":[""" + many + "]}";
        var (storage, movies) = await CreateAsync();
        using var _ = storage;
        await HeldMovieAsync(movies, "Many Releases", plan: plan);

        var item = Assert.Single(await new SqliteHeldReleaseReader(storage.Factory).ListAsync(50, CancellationToken.None));

        Assert.Equal("https://indexer.example/get/best", item.DownloadUrl);
    }

    [Fact]
    public async Task An_answered_held_release_is_forgotten_only_when_it_stops_being_held_and_never_by_the_other_list()
    {
        var (storage, _) = await CreateAsync();
        using var _ = storage;
        var store = new AttentionDismissalStore(storage.Factory, new FixedTimeProvider(Now));
        var held = AttentionDismissalStore.HeldReleaseKey("h1");
        var noRelease = AttentionDismissalStore.NoReleaseKey("movie", "m1");
        await store.DismissAsync(held, CancellationToken.None);
        await store.DismissAsync(noRelease, CancellationToken.None);

        // The no-release list pruning itself leaves the held dismissal alone.
        await store.ForgetExceptAsync(new HashSet<string>(), CancellationToken.None);
        Assert.Equal([held], (await store.ListAsync(CancellationToken.None)).ToArray());

        await store.ForgetExceptAsync(new HashSet<string>(), CancellationToken.None, AttentionDismissalStore.HeldReleasePrefix);
        Assert.Empty(await store.ListAsync(CancellationToken.None));
    }
}
