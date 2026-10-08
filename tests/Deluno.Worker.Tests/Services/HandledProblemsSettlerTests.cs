using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Movies.Data;
using Deluno.Series.Data;
using Deluno.Worker.Services;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// #1167: a problem whose title has its file now closes itself, with one Activity line each. The rig's Nosferatu
/// kept a Deluge dispatch whose import had failed, long after the film was imported another way.
/// </summary>
public sealed class HandledProblemsSettlerTests
{
    private static readonly DateTimeOffset Created = DateTimeOffset.Parse("2026-10-07T10:00:00Z");

    private readonly Mock<IMovieCatalogRepository> _movies = new();
    private readonly Mock<ISeriesCatalogRepository> _series = new();
    private readonly Mock<IJobQueueRepository> _jobs = new();
    private readonly Mock<IDownloadDispatchesRepository> _dispatches = new();
    private readonly Mock<IActivityFeedRepository> _activity = new();
    private readonly List<(string Category, string Message)> _said = [];

    public HandledProblemsSettlerTests()
    {
        _movies.Setup(repo => repo.SettleCasesOfTitlesHoldingFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _series.Setup(repo => repo.SettleCasesOfTitlesHoldingFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _movies.Setup(repo => repo.GetTitleFileHoldsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(TitleFileHolds.None);
        _series.Setup(repo => repo.GetTitleFileHoldsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(TitleFileHolds.None);
        _activity.Setup(feed => feed.RecordActivityAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, string?, string?, string?, CancellationToken>((kind, said, _, _, _, _, _) => _said.Add((kind, said)))
            .ReturnsAsync((ActivityEventItem)null!);
    }

    private HandledProblemsSettler Settler() => new(_movies.Object, _series.Object, _jobs.Object, _dispatches.Object, _activity.Object);

    private static DownloadDispatchItem FailedImport(string id, string entityType, string entityId)
        => new(id, "lib", "movies", entityType, entityId, "Nosferatu.1922.1080p.BluRay.x264-HD4U", "PublicHD", "client-1", "Deluge",
            "completed", null, Created, "succeeded", Created, 200, null, null, null, Created, "hash", 1, "failed", Created, Created, null,
            "importFailed", "Deluno import failed: The destination file already exists.", null);

    private void Listed(params DownloadDispatchItem[] items)
        => _jobs.Setup(repo => repo.ListDownloadDispatchesAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<string?>()))
            .ReturnsAsync(items);

    private static TitleFileHolds Holds(string id, DateTimeOffset? imported)
        => new(new Dictionary<string, DateTimeOffset?> { [id] = imported });

    [Fact]
    public async Task Each_case_it_closes_gets_one_line_in_plain_words()
    {
        _movies.Setup(repo => repo.SettleCasesOfTitlesHoldingFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SettledRecoveryCase("case-1", "Nosferatu.1922.1080p.BluRay.x264-HD4U [PublicHD]", "importFailed")]);
        _series.Setup(repo => repo.SettleCasesOfTitlesHoldingFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SettledRecoveryCase("case-2", "The Twilight Zone S01E02", "downloadFailed")]);
        Listed();

        Assert.Equal(2, await Settler().SettleAsync(CancellationToken.None));

        Assert.Equal(2, _said.Count);
        Assert.All(_said, line => Assert.Equal("recovery.case-settled", line.Category));
        Assert.Contains("is in the library now", _said[0].Message);
    }

    [Fact]
    public async Task A_failed_import_of_a_download_whose_film_holds_its_file_since_is_closed_once_with_a_line()
    {
        Listed(FailedImport("d-1", "movie", "movie-1"));
        _movies.Setup(repo => repo.GetTitleFileHoldsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Holds("movie-1", Created.AddHours(20)));

        Assert.Equal(1, await Settler().SettleAsync(CancellationToken.None));

        _dispatches.Verify(repo => repo.RecordImportOutcomeAsync("d-1", "imported", It.IsAny<string?>(), null, null, It.IsAny<CancellationToken>(), null), Times.Once);
        var line = Assert.Single(_said);
        Assert.Equal("download.import-failure-settled", line.Category);
        Assert.Contains("in the library now", line.Message);
    }

    [Fact]
    public async Task A_relinked_episode_whose_arrival_was_never_dated_closes_its_failure_too()
    {
        Listed(FailedImport("d-2", "episode", "episode-1"));
        _series.Setup(repo => repo.GetTitleFileHoldsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Holds("episode-1", null));

        Assert.Equal(1, await Settler().SettleAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_file_that_was_there_before_the_failure_does_not_hide_it()
    {
        Listed(FailedImport("d-3", "movie", "movie-1"));
        _movies.Setup(repo => repo.GetTitleFileHoldsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Holds("movie-1", Created.AddDays(-30)));

        Assert.Equal(0, await Settler().SettleAsync(CancellationToken.None));

        _dispatches.Verify(repo => repo.RecordImportOutcomeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<IntegrationFailure?>()), Times.Never);
        Assert.Empty(_said);
    }
}
