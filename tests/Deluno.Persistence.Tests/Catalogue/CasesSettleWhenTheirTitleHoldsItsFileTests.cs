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
/// An open "finished, not imported" or "import failed" case closes once its film or episode holds its file (#1167).
///
/// <para>On the rig, Nosferatu (imported) and The Twilight Zone S01E02 (relinked) kept their cases for ever. Both
/// had no details, and the settle that exists matches a case to its download by its details, so nothing matched.
/// The title is the key here, with or without details.</para>
/// </summary>
public sealed class CasesSettleWhenTheirTitleHoldsItsFileTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-08T00:00:00Z");

    private static async Task<(SqliteMovieCatalogRepository Movies, FixedTimeProvider Clock)> MoviesAsync(TestStorage storage)
    {
        var clock = new FixedTimeProvider(Start);
        await new MoviesSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, clock), NullLogger<MoviesSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);
        return (new SqliteMovieCatalogRepository(storage.Factory, clock), clock);
    }

    private static Task<bool> ImportFilmAsync(SqliteMovieCatalogRepository movies, string title, int year)
        => movies.ImportExistingAsync(
            libraryId: "library-films",
            title: title,
            releaseYear: year,
            wantedStatus: WantedStatuses.Covered,
            wantedReason: "imported",
            currentQuality: "Bluray-1080p",
            targetQuality: "Bluray-1080p",
            qualityCutoffMet: true,
            filePath: $"/library/{title} ({year})/{title} ({year}).mkv",
            fileSizeBytes: 8_000_000_000,
            cancellationToken: CancellationToken.None);

    private static Task<MovieImportRecoveryCase> FilmCaseAsync(SqliteMovieCatalogRepository movies, string title, string kind, string? details)
        => movies.AddImportRecoveryCaseAsync(
            new CreateMovieImportRecoveryCaseRequest(title, kind, "The destination file already exists.", "Look at the file.", details),
            CancellationToken.None);

    private static async Task<string[]> OpenAsync(SqliteMovieCatalogRepository movies)
        => (await movies.GetImportRecoverySummaryAsync(CancellationToken.None)).RecentCases.Select(item => item.Id).ToArray();

    [Fact]
    public async Task A_film_case_with_no_details_closes_once_the_film_holds_its_file()
    {
        using var storage = TestStorage.Create();
        var (movies, clock) = await MoviesAsync(storage);
        // Nosferatu on the rig: the case names the release and carries no details.
        var nosferatu = await FilmCaseAsync(movies, "Nosferatu.1922.1080p.BluRay.x264-HD4U [PublicHD]", "importFailed", null);
        var other = await FilmCaseAsync(movies, "Big.Buck.Bunny.2008.1080p.WEB-DL-DELUNO", "importFailed", null);
        Assert.Empty(await movies.SettleCasesOfTitlesHoldingFilesAsync(CancellationToken.None));

        clock.Advance(TimeSpan.FromHours(1));
        Assert.True(await ImportFilmAsync(movies, "Nosferatu", 1922));
        var settled = await movies.SettleCasesOfTitlesHoldingFilesAsync(CancellationToken.None);

        var one = Assert.Single(settled);
        Assert.Equal(nosferatu.Id, one.CaseId);
        var open = await OpenAsync(movies);
        Assert.DoesNotContain(nosferatu.Id, open);
        Assert.Contains(other.Id, open);
        Assert.Equal("resolved", (await movies.GetImportRecoveryCaseAsync(nosferatu.Id, CancellationToken.None))?.Status);
        // Settling twice finds nothing the second time.
        Assert.Empty(await movies.SettleCasesOfTitlesHoldingFilesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_film_case_with_details_closes_the_same_way()
    {
        using var storage = TestStorage.Create();
        var (movies, clock) = await MoviesAsync(storage);
        var withDetails = await FilmCaseAsync(movies, "Nosferatu.1922.1080p.BluRay.x264-HD4U", "downloadFailed", """{"DispatchId":"d-gone"}""");
        clock.Advance(TimeSpan.FromHours(1));
        await ImportFilmAsync(movies, "Nosferatu", 1922);

        Assert.Single(await movies.SettleCasesOfTitlesHoldingFilesAsync(CancellationToken.None));
        Assert.DoesNotContain(withDetails.Id, await OpenAsync(movies));
    }

    [Fact]
    public async Task A_film_of_another_year_or_a_case_about_the_file_itself_stays_open()
    {
        using var storage = TestStorage.Create();
        var (movies, clock) = await MoviesAsync(storage);
        var remake = await FilmCaseAsync(movies, "Nosferatu.2024.1080p.WEB-DL-DELUNO", "importFailed", null);
        var quality = await FilmCaseAsync(movies, "Nosferatu.1922.1080p.BluRay.x264-HD4U", "quality", null);
        clock.Advance(TimeSpan.FromHours(1));
        await ImportFilmAsync(movies, "Nosferatu", 1922);

        Assert.Empty(await movies.SettleCasesOfTitlesHoldingFilesAsync(CancellationToken.None));
        var open = await OpenAsync(movies);
        Assert.Contains(remake.Id, open);
        Assert.Contains(quality.Id, open);
    }

    /// <summary>A copy that was already there before the problem began says nothing about a failed upgrade.</summary>
    [Fact]
    public async Task A_file_that_was_there_before_the_problem_does_not_hide_it()
    {
        using var storage = TestStorage.Create();
        var (movies, clock) = await MoviesAsync(storage);
        await ImportFilmAsync(movies, "Nosferatu", 1922);
        clock.Advance(TimeSpan.FromHours(3));
        var failedUpgrade = await FilmCaseAsync(movies, "Nosferatu.1922.2160p.UHD.BluRay.x265-GRP", "importFailed", null);

        Assert.Empty(await movies.SettleCasesOfTitlesHoldingFilesAsync(CancellationToken.None));
        Assert.Contains(failedUpgrade.Id, await OpenAsync(movies));
    }

    [Fact]
    public async Task An_episode_case_with_no_details_closes_once_the_episode_holds_its_file()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        await new SeriesSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, clock), NullLogger<SeriesSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);
        var series = new SqliteSeriesCatalogRepository(storage.Factory, clock);
        // The Twilight Zone S01E02 on the rig: a download that finished and was never imported, no details.
        var s01e02 = await series.AddImportRecoveryCaseAsync(
            new CreateSeriesImportRecoveryCaseRequest("www.UIndex.org - The Twilight Zone S01E02 One for the Angels 1080p AMZN WEB-DL-playWEB", "downloadFailed", "The download finished 4 hours ago and has not been imported.", "Import it.", null),
            CancellationToken.None);
        var s01e03 = await series.AddImportRecoveryCaseAsync(
            new CreateSeriesImportRecoveryCaseRequest("The Twilight Zone S01E03 Mr Denton on Doomsday 1080p", "downloadFailed", "Not imported.", "Import it.", null),
            CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(4));
        Assert.True(await series.ImportExistingAsync(
            libraryId: "library-shows",
            title: "The Twilight Zone",
            startYear: 1959,
            wantedStatus: WantedStatuses.Covered,
            wantedReason: "imported",
            currentQuality: "WEB 1080p",
            targetQuality: "WEB 1080p",
            qualityCutoffMet: true,
            filePath: "/library/The Twilight Zone (1959)/Season 01/S01E02.mkv",
            fileSizeBytes: 1_000_000_000,
            episodes: [new ImportedEpisodeItem(1, 2, HasFile: true, FilePath: "/library/The Twilight Zone (1959)/Season 01/S01E02.mkv", FileSizeBytes: 1_000_000_000), new ImportedEpisodeItem(1, 3, HasFile: false)],
            cancellationToken: CancellationToken.None));

        var settled = await series.SettleCasesOfTitlesHoldingFilesAsync(CancellationToken.None);

        Assert.Equal(s01e02.Id, Assert.Single(settled).CaseId);
        var open = (await series.GetImportRecoverySummaryAsync(CancellationToken.None)).RecentCases.Select(item => item.Id).ToArray();
        Assert.DoesNotContain(s01e02.Id, open);
        Assert.Contains(s01e03.Id, open);
    }

    [Fact]
    public async Task The_files_a_title_holds_are_told_by_id_with_when_they_arrived()
    {
        using var storage = TestStorage.Create();
        var (movies, clock) = await MoviesAsync(storage);
        await ImportFilmAsync(movies, "Nosferatu", 1922);
        var film = (await movies.ListPageAsync(new CatalogueQuery(), CancellationToken.None)).Items.Single();

        var holds = await movies.GetTitleFileHoldsAsync([film.Id, "no-such-film"], CancellationToken.None);

        Assert.True(holds.ImportedByEntityId.ContainsKey(film.Id));
        Assert.False(holds.ImportedByEntityId.ContainsKey("no-such-film"));
        Assert.Empty((await movies.GetTitleFileHoldsAsync([], CancellationToken.None)).ImportedByEntityId);
        _ = clock;
    }
}
