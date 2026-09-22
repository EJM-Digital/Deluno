using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Catalogue;

/// <summary>
/// Removing a title settles the open cases about its downloads (#563).
///
/// <para><b>Why this file exists.</b> On the rig the proof titles were removed,
/// and nine cases raised about their downloads stayed open and counted in the
/// sidebar until each was dismissed by hand. A case records a release name and
/// some details and nothing else — no case says which film or show it belongs
/// to — so there is nothing for a removal to settle them by unless it names the
/// downloads itself. These tests pin the three ways a case says what it is
/// about: the poller's <c>DispatchId</c>, the processor watcher's
/// <c>releaseName</c>, and the failed import's nested retry.</para>
/// </summary>
public sealed class RemovingATitleSettlesItsRecoveryCasesTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-23T00:00:00Z");
    private const string Removed = "Tears of Steel was removed from Deluno, so this is no longer something to answer.";
    private const string Release = "Tears.of.Steel.2012.1080p.WEB-DL.DDP5.1.H.264-DELUNO";

    [Fact]
    public async Task Every_case_about_the_titles_downloads_closes_and_nothing_else_does()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        await new MoviesSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, clock), NullLogger<MoviesSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, clock);
        // What the download poller writes.
        var grabTimeout = await AddAsync(movies, Release, """{"DispatchId":"d1","DownloadClientName":"SABnzbd"}""");
        // What the processor watcher writes: a hand-off, never a dispatch.
        var processorTimeout = await AddAsync(movies, Release, $$"""{"libraryId":"lib-1","handoffId":"h1","releaseName":"{{Release}}"}""");
        // What a failed import writes: the dispatch is inside the retry it offers.
        var failedImport = await AddAsync(movies, Release, """{"RetryRequest":{"DispatchId":"d1"},"FileName":"Tears.of.Steel.mkv"}""");
        // Another title's download, and a case whose details cannot be read.
        var somebodyElse = await AddAsync(movies, "Big.Buck.Bunny.2008.1080p.WEB-DL-DELUNO", """{"DispatchId":"d2","releaseName":"Big.Buck.Bunny.2008.1080p.WEB-DL-DELUNO"}""");
        var unreadable = await AddAsync(movies, Release, "not json");

        var settled = await movies.SettleImportRecoveryCasesAsync(
            ImportRecoveryCaseScope.ForRemovedTitle(["d1"], [Release]),
            Removed,
            CancellationToken.None);

        Assert.Equal(3, settled);
        var open = (await movies.GetImportRecoverySummaryAsync(CancellationToken.None)).RecentCases.Select(item => item.Id).ToArray();
        Assert.DoesNotContain(grabTimeout.Id, open);
        Assert.DoesNotContain(processorTimeout.Id, open);
        Assert.DoesNotContain(failedImport.Id, open);
        Assert.Contains(somebodyElse.Id, open);
        Assert.Contains(unreadable.Id, open);
    }

    /// <summary>Each settled case says why, so its history is not a silent close.</summary>
    [Fact]
    public async Task A_settled_case_records_the_removal_that_settled_it()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        await new MoviesSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, clock), NullLogger<MoviesSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, clock);
        var raised = await AddAsync(movies, Release, $$"""{"handoffId":"h1","releaseName":"{{Release}}"}""");

        await movies.SettleImportRecoveryCasesAsync(
            ImportRecoveryCaseScope.ForRemovedTitle([], [Release]),
            Removed,
            CancellationToken.None);

        var stored = await movies.GetImportRecoveryCaseAsync(raised.Id, CancellationToken.None);
        Assert.Equal("resolved", stored?.Status);
        Assert.Equal(Start, stored?.ResolvedUtc);
    }

    [Fact]
    public async Task A_shows_cases_close_the_same_way()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        await new SeriesSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, clock), NullLogger<SeriesSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);
        var series = new SqliteSeriesCatalogRepository(storage.Factory, clock);
        const string episode = "Pioneer.One.S01E01.1080p.WEB-DL.DDP5.1.H.264-DELUNO";
        var raised = await series.AddImportRecoveryCaseAsync(
            new CreateSeriesImportRecoveryCaseRequest(episode, "processor-timeout", "Weir never finished it.", "Check the processor.", $$"""{"handoffId":"h1","releaseName":"{{episode}}"}"""),
            CancellationToken.None);

        var settled = await series.SettleImportRecoveryCasesAsync(
            ImportRecoveryCaseScope.ForRemovedTitle(["d1"], [episode]),
            "Pioneer One was removed from Deluno, so this is no longer something to answer.",
            CancellationToken.None);

        Assert.Equal(1, settled);
        Assert.DoesNotContain(raised.Id, (await series.GetImportRecoverySummaryAsync(CancellationToken.None)).RecentCases.Select(item => item.Id));
    }

    /// <summary>A title that never fetched anything asks the database nothing.</summary>
    [Fact]
    public async Task A_title_with_no_downloads_settles_nothing()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        await new MoviesSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, clock), NullLogger<MoviesSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, clock);
        var untouched = await AddAsync(movies, Release, """{"DispatchId":"d1"}""");

        Assert.Equal(0, await movies.SettleImportRecoveryCasesAsync(ImportRecoveryCaseScope.ForRemovedTitle([], []), Removed, CancellationToken.None));
        Assert.Contains(untouched.Id, (await movies.GetImportRecoverySummaryAsync(CancellationToken.None)).RecentCases.Select(item => item.Id));
    }

    private static Task<MovieImportRecoveryCase> AddAsync(SqliteMovieCatalogRepository movies, string title, string detailsJson)
        => movies.AddImportRecoveryCaseAsync(
            new CreateMovieImportRecoveryCaseRequest(title, "grab-timeout", "Never appeared.", "Check the client.", detailsJson),
            CancellationToken.None);
}
