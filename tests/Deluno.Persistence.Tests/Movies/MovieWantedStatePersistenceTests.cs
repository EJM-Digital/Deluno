using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Infrastructure.Storage.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Movies;

public sealed class MovieWantedStatePersistenceTests
{
    [Fact]
    public async Task AddAsync_returns_existing_movie_for_duplicate_identity()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T02:00:00Z"));

        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);

        var first = await repository.AddAsync(
            new CreateMovieRequest(
                Title: "Dune Part Two",
                ReleaseYear: 2024,
                ImdbId: "tt15239678",
                MetadataProvider: "tmdb",
                MetadataProviderId: "693134"),
            CancellationToken.None);

        var duplicate = await repository.AddAsync(
            new CreateMovieRequest(
                Title: "dune part two",
                ReleaseYear: 2024,
                ImdbId: "tt15239678",
                MetadataProvider: "tmdb",
                MetadataProviderId: "693134"),
            CancellationToken.None);

        Assert.Equal(first.Id, duplicate.Id);
        Assert.Single(await repository.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task EnsureWantedStateAsync_creates_and_updates_one_state_per_movie_and_library()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T02:00:00Z"));

        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var movie = await repository.AddAsync(
            new CreateMovieRequest("Dune Part Two", 2024, "tt15239678"),
            CancellationToken.None);

        await repository.EnsureWantedStateAsync(
            movie.Id,
            libraryId: "movies-main",
            wantedStatus: "missing",
            wantedReason: "No accepted file exists.",
            hasFile: false,
            currentQuality: null,
            targetQuality: "WEB 1080p",
            qualityCutoffMet: false,
            CancellationToken.None);

        await repository.EnsureWantedStateAsync(
            movie.Id,
            libraryId: "movies-main",
            wantedStatus: "upgrade",
            wantedReason: "Existing file is below cutoff.",
            hasFile: true,
            currentQuality: "WEB 720p",
            targetQuality: "WEB 1080p",
            qualityCutoffMet: false,
            CancellationToken.None);

        var summary = await repository.GetWantedSummaryAsync(CancellationToken.None);
        var item = Assert.Single(summary.RecentItems);

        Assert.Equal(1, summary.TotalWanted);
        Assert.Equal(0, summary.MissingCount);
        Assert.Equal(1, summary.UpgradeCount);
        Assert.Equal(movie.Id, item.MovieId);
        Assert.Equal("movies-main", item.LibraryId);
        Assert.Equal("upgrade", item.WantedStatus);
        Assert.True(item.HasFile);
        Assert.Equal("WEB 720p", item.CurrentQuality);
        Assert.Equal("WEB 1080p", item.TargetQuality);
        Assert.NotNull(item.MissingSinceUtc);
    }

    /// <summary>
    /// A paused (unmonitored) movie is skipped by every automatic path, even
    /// one targeted at it that ignores the retry window - an add-triggered
    /// search or a health-replacement retry is still automatic (#653). Only a
    /// person's own retry, which passes <c>requireMonitored: false</c>, still
    /// reaches it.
    /// </summary>
    [Fact]
    public async Task Paused_movie_is_skipped_by_every_automatic_path_but_remains_available_to_manual_search()
    {
        using var storage = TestStorage.Create();
        var now = DateTimeOffset.Parse("2026-08-13T00:00:00Z");
        var timeProvider = new FixedTimeProvider(now);
        await new MoviesSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, timeProvider), NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var movie = await repository.AddAsync(new CreateMovieRequest("Dune Part Two", 2024, "tt15239678"), CancellationToken.None);
        await repository.EnsureWantedStateAsync(movie.Id, "movies-main", "missing", "Needs a file.", false, null, "WEB 1080p", false, CancellationToken.None);

        await repository.UpdateMonitoredAsync([movie.Id], monitored: false, CancellationToken.None);

        Assert.Empty(await repository.ListEligibleWantedAsync("movies-main", 10, now, ignoreRetryWindow: false, CancellationToken.None));
        Assert.Empty(await repository.ListEligibleWantedAsync("movies-main", 10, now, ignoreRetryWindow: true, CancellationToken.None));
        Assert.Single(await repository.ListEligibleWantedAsync("movies-main", 10, now, ignoreRetryWindow: true, CancellationToken.None, requireMonitored: false));
    }

    [Fact]
    public async Task Deferred_movie_is_skipped_until_its_explicit_automation_window_expires()
    {
        using var storage = TestStorage.Create();
        var now = DateTimeOffset.Parse("2026-08-13T00:00:00Z");
        var timeProvider = new FixedTimeProvider(now);
        await new MoviesSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, timeProvider), NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var movie = await repository.AddAsync(new CreateMovieRequest("Dune Part Two", 2024, "tt15239678"), CancellationToken.None);
        await repository.EnsureWantedStateAsync(movie.Id, "movies-main", "missing", "Needs a file.", false, null, "WEB 1080p", false, CancellationToken.None);
        var deferredUntil = now.AddHours(24);

        Assert.True(await repository.DeferWantedSearchAsync(movie.Id, "movies-main", deferredUntil, CancellationToken.None));
        Assert.Empty(await repository.ListEligibleWantedAsync("movies-main", 10, now, ignoreRetryWindow: false, CancellationToken.None));
        Assert.Single(await repository.ListEligibleWantedAsync("movies-main", 10, now, ignoreRetryWindow: true, CancellationToken.None));
        Assert.Equal(deferredUntil, Assert.Single((await repository.GetWantedSummaryAsync(CancellationToken.None)).RecentItems).NextEligibleSearchUtc);
    }

    [Fact]
    public async Task Asking_for_one_kind_returns_only_titles_of_that_kind_up_to_the_limit()
    {
        using var storage = TestStorage.Create();
        var now = DateTimeOffset.Parse("2026-08-13T00:00:00Z");
        var timeProvider = new FixedTimeProvider(now);
        await new MoviesSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, timeProvider), NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        for (var i = 0; i < 3; i++)
        {
            var missing = await repository.AddAsync(new CreateMovieRequest($"Missing {i}", 2024, $"tt1{i:000000}"), CancellationToken.None);
            await repository.EnsureWantedStateAsync(missing.Id, "movies-main", "missing", "Needs a file.", false, null, "WEB 1080p", false, CancellationToken.None);
            var upgrade = await repository.AddAsync(new CreateMovieRequest($"Upgrade {i}", 2023, $"tt2{i:000000}"), CancellationToken.None);
            await repository.EnsureWantedStateAsync(upgrade.Id, "movies-main", "upgrade", "Below the cutoff.", true, "WEB 720p", "WEB 1080p", false, CancellationToken.None);
        }

        var missingOnly = await repository.ListEligibleWantedAsync("movies-main", 2, now, ignoreRetryWindow: true, CancellationToken.None, wantedStatus: "missing");
        var upgradeOnly = await repository.ListEligibleWantedAsync("movies-main", 10, now, ignoreRetryWindow: true, CancellationToken.None, wantedStatus: "upgrade");

        Assert.Equal(2, missingOnly.Count);
        Assert.All(missingOnly, item => Assert.Equal("missing", item.WantedStatus));
        Assert.Equal(3, upgradeOnly.Count);
        Assert.All(upgradeOnly, item => Assert.Equal("upgrade", item.WantedStatus));
    }

    [Fact]
    public async Task Skip_next_movie_search_is_durable_and_is_consumed_exactly_once()
    {
        using var storage = TestStorage.Create();
        var now = DateTimeOffset.Parse("2026-08-13T00:00:00Z");
        var timeProvider = new FixedTimeProvider(now);
        await new MoviesSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, timeProvider), NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var movie = await repository.AddAsync(new CreateMovieRequest("Dune Part Two", 2024, "tt15239678"), CancellationToken.None);
        await repository.EnsureWantedStateAsync(movie.Id, "movies-main", "missing", "Needs a file.", false, null, "WEB 1080p", false, CancellationToken.None);

        Assert.True(await repository.SkipNextWantedSearchAsync(movie.Id, "movies-main", CancellationToken.None));
        Assert.Equal("Will skip the next scheduled search by user request.", Assert.Single((await repository.GetWantedSummaryAsync(CancellationToken.None)).RecentItems).LastSearchResult);
        Assert.Single(await repository.ListEligibleWantedAsync("movies-main", 10, now, ignoreRetryWindow: true, CancellationToken.None));
        Assert.True(await repository.ConsumeSkipNextWantedSearchAsync(movie.Id, "movies-main", CancellationToken.None));
        Assert.False(await repository.ConsumeSkipNextWantedSearchAsync(movie.Id, "movies-main", CancellationToken.None));
    }

    [Fact]
    public async Task Eligible_wanted_movies_can_be_asked_for_in_random_order_instead_of_oldest_first()
    {
        using var storage = TestStorage.Create();
        var now = DateTimeOffset.Parse("2026-08-13T00:00:00Z");
        var timeProvider = new FixedTimeProvider(now);
        await new MoviesSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, timeProvider), NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var movieIds = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            var movie = await repository.AddAsync(new CreateMovieRequest($"Movie {i}", 2024, $"tt{i:0000000}"), CancellationToken.None);
            await repository.EnsureWantedStateAsync(movie.Id, "movies-main", "missing", "Needs a file.", false, null, "WEB 1080p", false, CancellationToken.None);
            movieIds.Add(movie.Id);
        }

        async Task<string[]> OrderAsync(bool randomOrder) =>
            (await repository.ListEligibleWantedAsync("movies-main", 10, now, ignoreRetryWindow: true, CancellationToken.None, randomOrder: randomOrder))
                .Select(item => item.MovieId)
                .ToArray();

        var oldestFirst = await OrderAsync(randomOrder: false);
        Assert.Equal(movieIds, oldestFirst);
        Assert.Equal(oldestFirst, await OrderAsync(randomOrder: false));

        var seenOrders = new HashSet<string>();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var order = await OrderAsync(randomOrder: true);
            Assert.Equal(movieIds.OrderBy(id => id), order.OrderBy(id => id));
            seenOrders.Add(string.Join(",", order));
        }

        Assert.True(seenOrders.Count > 1, "Random order never varied across 20 attempts.");
    }
}
