using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Moq;

namespace Deluno.Movies.Tests.Services;

/// <summary>
/// #507: every movie dispatch is recorded with the media type <c>movies</c>,
/// and the handler only answered to <c>movie</c>. So a movie download that
/// never reached the client, or whose import failed, raised an alert and never
/// the recovery case that says what to do about it.
/// </summary>
public sealed class StuckMovieDownloadsGetARecoveryCaseTests
{
    [Theory]
    [InlineData("movies")]
    [InlineData("movie")]
    public async Task A_movie_dispatch_records_a_case_for_every_stuck_state(string mediaType)
    {
        var catalog = new Mock<IMovieCatalogRepository>();
        var handler = new MovieDispatchRecoveryHandler(catalog.Object);

        await handler.HandleGrabTimeoutAsync("Sintel", mediaType, "qb", "qBittorrent", "Sintel.2010", "{}", CancellationToken.None);
        await handler.HandleDetectionTimeoutAsync("Sintel", mediaType, "qb", "qBittorrent", "Sintel.2010", "m1", "{}", CancellationToken.None);
        await handler.HandleImportTimeoutAsync("Sintel", mediaType, "qb", "qBittorrent", "Sintel.2010", "m1", "{}", CancellationToken.None);
        await handler.HandleImportFailureAsync("Sintel", mediaType, "qb", "qBittorrent", "Sintel.2010", "m1", "importFailed", "Unreadable", "{}", CancellationToken.None);
        await handler.HandleVanishedDownloadingAsync("Sintel", mediaType, "qb", "qBittorrent", "Sintel.2010", "m1", "{}", CancellationToken.None);

        catalog.Verify(
            repo => repo.AddImportRecoveryCaseAsync(It.IsAny<CreateMovieImportRecoveryCaseRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(5));
    }

    /// <summary>#927: the case is raised under its own kind, not folded into another one, so Search for another and Stop wanting it are offered rather than Try again's ordinary two branches.</summary>
    [Fact]
    public async Task A_vanished_download_records_its_own_kind()
    {
        CreateMovieImportRecoveryCaseRequest? captured = null;
        var catalog = new Mock<IMovieCatalogRepository>();
        catalog.Setup(repo => repo.AddImportRecoveryCaseAsync(It.IsAny<CreateMovieImportRecoveryCaseRequest>(), It.IsAny<CancellationToken>()))
            .Callback<CreateMovieImportRecoveryCaseRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync((MovieImportRecoveryCase)null!);
        var handler = new MovieDispatchRecoveryHandler(catalog.Object);

        await handler.HandleVanishedDownloadingAsync("Sintel", "movies", "qb", "qBittorrent", "Sintel.2010", "m1", "{}", CancellationToken.None);

        Assert.Equal("vanished-downloading", captured!.FailureKind);
    }

    [Fact]
    public async Task A_tv_dispatch_is_left_to_the_series_handler()
    {
        var catalog = new Mock<IMovieCatalogRepository>();
        var handler = new MovieDispatchRecoveryHandler(catalog.Object);

        await handler.HandleGrabTimeoutAsync("Pioneer One", "tv", "qb", "qBittorrent", "Pioneer.One.S01E01", "{}", CancellationToken.None);

        catalog.Verify(
            repo => repo.AddImportRecoveryCaseAsync(It.IsAny<CreateMovieImportRecoveryCaseRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
