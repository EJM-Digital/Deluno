using Deluno.Platform.Migration;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Series.Migration;
using Moq;

namespace Deluno.Series.Tests.Migration;

public sealed class SeriesMigrationCatalogImporterTests
{
    [Fact]
    public async Task ImportAsync_checks_each_incoming_title_without_reading_the_whole_catalogue()
    {
        var repository = new Mock<ISeriesCatalogRepository>(MockBehavior.Strict);
        repository.Setup(item => item.FindExistingIdAsync(
                "Severance", 2022, "tt11280740", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        repository.Setup(item => item.AddAsync(It.IsAny<CreateSeriesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Series("series-1", "Severance"));
        repository.Setup(item => item.EnsureWantedStateAsync(
                "series-1", "tv-main", "missing", It.IsAny<string>(), false, null, null, false, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await new SeriesMigrationCatalogImporter(repository.Object).ImportAsync(
            Request("tv", "Severance", 2022, "tt11280740"),
            CancellationToken.None);

        Assert.Equal("created", Assert.Single(result.Applied).Result);
        repository.Verify(item => item.FindExistingIdAsync(
            "Severance", 2022, "tt11280740", null, null, It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(item => item.ListAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    private static MigrationCatalogImportRequest Request(string mediaType, string title, int year, string imdbId)
        => new(
            "sonarr",
            "Sonarr",
            [new MigrationCatalogTitle(mediaType, title, year, imdbId, null, null, true, false, "/media/tv")],
            [new MigrationCatalogLibrary("tv-main", mediaType, "/media/tv", "TV")]);

    private static SeriesListItem Series(string id, string title)
        => new(id, title, 2022, "tt11280740", true, false, null, null, null, null, null, null, null, [], null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static Mock<ISeriesCatalogRepository> Repo(string? existingId, string id = "id-1")
    {
        var repository = new Mock<ISeriesCatalogRepository>();
        repository.Setup(item => item.FindExistingIdAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingId);
        repository.Setup(item => item.AddAsync(It.IsAny<CreateSeriesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Series(id, "Heat"));
        return repository;
    }

    private static MigrationCatalogImportRequest Custom(IReadOnlyList<MigrationCatalogTitle> titles, params MigrationCatalogLibrary[] libraries)
        => new("source", "Source", titles, libraries);

    [Fact]
    public async Task A_title_already_in_the_catalogue_is_reported_skipped_not_created()
    {
        var repository = Repo("existing-1");

        var result = await new SeriesMigrationCatalogImporter(repository.Object).ImportAsync(Request("tv", "Heat", 1995, "tt0113277"), CancellationToken.None);

        Assert.Equal("skipped", Assert.Single(result.Applied).Result);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task A_title_the_source_says_has_a_file_is_kept_covered_and_waits_for_a_library_scan()
    {
        var repository = Repo(null);
        var titles = new[] { new MigrationCatalogTitle("tv", "Heat", 1995, null, null, null, true, SourceReportsFile: true, "/media/tv") };

        await new SeriesMigrationCatalogImporter(repository.Object).ImportAsync(Custom(titles, new MigrationCatalogLibrary("tv-main", "tv", "/media/tv", "Lib")), CancellationToken.None);

        repository.Verify(item => item.EnsureWantedStateAsync("id-1", "tv-main", "covered", It.Is<string>(note => note.Contains("library scan")), true, null, null, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_title_the_source_has_no_file_for_is_filed_as_missing()
    {
        var repository = Repo(null);
        var titles = new[] { new MigrationCatalogTitle("tv", "Heat", 1995, null, null, null, true, SourceReportsFile: false, "/media/tv") };

        await new SeriesMigrationCatalogImporter(repository.Object).ImportAsync(Custom(titles, new MigrationCatalogLibrary("tv-main", "tv", "/media/tv", "Lib")), CancellationToken.None);

        repository.Verify(item => item.EnsureWantedStateAsync("id-1", "tv-main", "missing", It.IsAny<string>(), false, null, null, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_title_with_no_root_folder_and_two_libraries_is_not_imported_and_says_why()
    {
        var repository = Repo(null);
        var titles = new[] { new MigrationCatalogTitle("tv", "Heat", 1995, null, null, null, true, false, RootPath: null) };

        var result = await new SeriesMigrationCatalogImporter(repository.Object).ImportAsync(
            Custom(titles, new MigrationCatalogLibrary("a", "tv", "/one", "One"), new MigrationCatalogLibrary("b", "tv", "/two", "Two")), CancellationToken.None);

        Assert.Empty(result.Applied);
        Assert.Equal("Did not import TV show 'Heat' because Deluno could not safely choose one TV library. Review its root-folder mapping first.", Assert.Single(result.Warnings));
        repository.Verify(item => item.AddAsync(It.IsAny<CreateSeriesRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_title_with_no_root_folder_goes_to_the_only_library_and_a_root_folder_is_matched_ignoring_case()
    {
        var repository = Repo(null);
        var titles = new[]
        {
            new MigrationCatalogTitle("tv", "Heat", 1995, null, null, null, true, false, RootPath: null),
            new MigrationCatalogTitle("tv", "Heat", 1995, null, null, null, true, false, RootPath: "/MEDIA/TV"),
        };

        var result = await new SeriesMigrationCatalogImporter(repository.Object).ImportAsync(Custom(titles, new MigrationCatalogLibrary("tv-main", "tv", "/media/tv", "Lib")), CancellationToken.None);

        Assert.Equal(2, result.Applied.Count);
        repository.Verify(item => item.EnsureWantedStateAsync("id-1", "tv-main", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), null, null, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Titles_of_the_other_media_type_are_ignored_by_this_importer()
    {
        var repository = Repo(null);
        var titles = new[] { new MigrationCatalogTitle("movies", "Heat", 1995, null, null, null, true, false, "/media/tv") };

        var result = await new SeriesMigrationCatalogImporter(repository.Object).ImportAsync(Custom(titles, new MigrationCatalogLibrary("tv-main", "tv", "/media/tv", "Lib")), CancellationToken.None);

        Assert.Empty(result.Applied);
        Assert.Empty(result.Warnings);
        repository.Verify(item => item.AddAsync(It.IsAny<CreateSeriesRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
