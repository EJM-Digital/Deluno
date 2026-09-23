using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Movies.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Series.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Catalogue;

/// <summary>
/// #596: the same import failing again is the same recovery case.
///
/// <para><b>Why this file exists.</b> On the soak rig one import of
/// Breaking.Bad.S01E02 failed with "the destination file already exists" and
/// was retried twice. Each attempt opened its own case, and the Dashboard
/// counted three problems where there was one.</para>
/// </summary>
public sealed class TheSameImportFailureIsOneCaseTests
{
    private const string Title = "Breaking.Bad.S01E02.1080p.WEB-DL.DDP5.1.H.264-UPGRADE";
    private const string Exists = "The destination file already exists.";
    private const string Request = """{"RetryRequest":{"DispatchId":"d1"}}""";

    [Fact]
    public async Task A_show_import_that_fails_the_same_way_again_refreshes_its_open_case()
    {
        var (_, series, clock, storage) = await RepositoriesAsync();
        using var _ = storage;

        var first = await series.AddImportRecoveryCaseAsync(new CreateSeriesImportRecoveryCaseRequest(Title, "importFailed", Exists, null, Request), CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(1));
        var again = await series.AddImportRecoveryCaseAsync(new CreateSeriesImportRecoveryCaseRequest(Title, "importFailed", Exists, null, Request), CancellationToken.None);

        Assert.Equal(first.Id, again.Id);
        Assert.Equal(1, (await series.GetImportRecoverySummaryAsync(CancellationToken.None)).OpenCount);
        var stored = await series.GetImportRecoveryCaseAsync(first.Id, CancellationToken.None);
        Assert.Equal(Request, stored!.DetailsJson);
        Assert.Equal(clock.GetUtcNow(), stored!.DetectedUtc);
    }

    /// <summary>
    /// Another download of the same release is its own case, so settling one
    /// download does not close a case about another.
    /// </summary>
    [Fact]
    public async Task Another_download_of_the_same_release_keeps_its_own_case()
    {
        var (_, series, _, storage) = await RepositoriesAsync();
        using var _ = storage;

        var first = await series.AddImportRecoveryCaseAsync(new CreateSeriesImportRecoveryCaseRequest(Title, "importFailed", Exists, null, Request), CancellationToken.None);
        var second = await series.AddImportRecoveryCaseAsync(new CreateSeriesImportRecoveryCaseRequest(Title, "importFailed", Exists, null, """{"RetryRequest":{"DispatchId":"d2"}}"""), CancellationToken.None);

        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task A_different_failure_or_one_after_the_case_closed_is_a_new_case()
    {
        var (_, series, _, storage) = await RepositoriesAsync();
        using var _ = storage;

        var first = await series.AddImportRecoveryCaseAsync(new CreateSeriesImportRecoveryCaseRequest(Title, "importFailed", Exists, null, null), CancellationToken.None);
        var other = await series.AddImportRecoveryCaseAsync(new CreateSeriesImportRecoveryCaseRequest(Title, "importFailed", "The source file is gone.", null, null), CancellationToken.None);
        await series.ResolveImportRecoveryCaseAsync(first.Id, "resolved", CancellationToken.None);
        var reopened = await series.AddImportRecoveryCaseAsync(new CreateSeriesImportRecoveryCaseRequest(Title, "importFailed", Exists, null, null), CancellationToken.None);

        Assert.NotEqual(first.Id, other.Id);
        Assert.NotEqual(first.Id, reopened.Id);
        Assert.Equal(2, (await series.GetImportRecoverySummaryAsync(CancellationToken.None)).OpenCount);
    }

    [Fact]
    public async Task A_film_import_that_fails_the_same_way_again_refreshes_its_open_case()
    {
        var (movies, _, _, storage) = await RepositoriesAsync();
        using var _ = storage;

        var first = await movies.AddImportRecoveryCaseAsync(new CreateMovieImportRecoveryCaseRequest("Nosferatu.1922.1080p", "importFailed", Exists, null, null), CancellationToken.None);
        var again = await movies.AddImportRecoveryCaseAsync(new CreateMovieImportRecoveryCaseRequest("Nosferatu.1922.1080p", "importFailed", Exists, null, null), CancellationToken.None);

        Assert.Equal(first.Id, again.Id);
        Assert.Equal(1, (await movies.GetImportRecoverySummaryAsync(CancellationToken.None)).OpenCount);
    }

    private static async Task<(SqliteMovieCatalogRepository Movies, SqliteSeriesCatalogRepository Series, FixedTimeProvider Clock, TestStorage Storage)> RepositoriesAsync()
    {
        var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-23T12:45:00Z"));
        await new MoviesSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, clock), NullLogger<MoviesSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);
        await new SeriesSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, clock), NullLogger<SeriesSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);
        return (new SqliteMovieCatalogRepository(storage.Factory, clock), new SqliteSeriesCatalogRepository(storage.Factory, clock), clock, storage);
    }
}
