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
/// A recovery case closes when the download it describes recovers (#559).
///
/// <para>On the rig, eight downloads Deluno had called "never shown by the
/// client" were recognised after all: each came back, and its alert closed
/// itself. The eight cases beside those alerts stayed open, still saying
/// "never appeared in the client's queue", and the sidebar went on counting
/// them.</para>
/// </summary>
public sealed class RecoveryCasesSettleWithTheirDownloadTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-22T00:00:00Z");
    private const string Reason = "The download client picked it up.";

    [Fact]
    public async Task Only_the_open_cases_about_that_download_close()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        await new MoviesSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, clock), NullLogger<MoviesSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, clock);
        var recovered = await AddCaseAsync(movies, """{"DispatchId":"d1","DownloadClientName":"SABnzbd"}""");
        var other = await AddCaseAsync(movies, """{"DispatchId":"d2"}""");
        var unreadable = await AddCaseAsync(movies, "not json");

        var settled = await movies.SettleImportRecoveryCasesAsync(ImportRecoveryCaseScope.ForDispatch("d1"), Reason, CancellationToken.None);

        Assert.Equal(1, settled);
        var open = (await movies.GetImportRecoverySummaryAsync(CancellationToken.None)).RecentCases.Select(item => item.Id).ToArray();
        Assert.DoesNotContain(recovered.Id, open);
        Assert.Contains(other.Id, open);
        Assert.Contains(unreadable.Id, open);
        // Settling twice finds nothing the second time.
        Assert.Equal(0, await movies.SettleImportRecoveryCasesAsync(ImportRecoveryCaseScope.ForDispatch("d1"), Reason, CancellationToken.None));
    }

    /// <summary>
    /// A download recovering says nothing about an import that failed, so the
    /// case offering to retry that import stays open. Only a removed title takes
    /// those (#563).
    /// </summary>
    [Fact]
    public async Task A_recovered_download_leaves_the_failed_import_it_was_not_about()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        await new MoviesSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, clock), NullLogger<MoviesSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, clock);
        var failedImport = await AddCaseAsync(movies, """{"RetryRequest":{"DispatchId":"d1"},"FileName":"Tears.of.Steel.mkv"}""");

        Assert.Equal(0, await movies.SettleImportRecoveryCasesAsync(ImportRecoveryCaseScope.ForDispatch("d1"), Reason, CancellationToken.None));
        Assert.Contains(failedImport.Id, (await movies.GetImportRecoverySummaryAsync(CancellationToken.None)).RecentCases.Select(item => item.Id));
    }

    [Fact]
    public async Task A_show_case_closes_the_same_way()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        await new SeriesSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, clock), NullLogger<SeriesSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);
        var series = new SqliteSeriesCatalogRepository(storage.Factory, clock);
        var recovered = await series.AddImportRecoveryCaseAsync(
            new CreateSeriesImportRecoveryCaseRequest("Pioneer.One.S01E01.1080p.WEB-DL-DELUNO", "grab-timeout", "Never appeared.", "Check the client.", """{"DispatchId":"d9"}"""),
            CancellationToken.None);

        var handler = new SeriesDispatchRecoveryHandler(series);
        await handler.HandleDispatchSettledAsync("d9", Reason, CancellationToken.None);

        Assert.DoesNotContain(recovered.Id, (await series.GetImportRecoverySummaryAsync(CancellationToken.None)).RecentCases.Select(item => item.Id));
    }

    private static Task<MovieImportRecoveryCase> AddCaseAsync(SqliteMovieCatalogRepository movies, string detailsJson)
        => movies.AddImportRecoveryCaseAsync(
            new CreateMovieImportRecoveryCaseRequest("Tears.of.Steel.2012.1080p.WEB-DL-DELUNO", "grab-timeout", "Never appeared.", "Check the client.", detailsJson),
            CancellationToken.None);
}
