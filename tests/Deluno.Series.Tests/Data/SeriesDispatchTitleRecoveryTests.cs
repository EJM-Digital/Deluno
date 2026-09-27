using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Moq;

namespace Deluno.Series.Tests.Data;

/// <summary>
/// #927: a vanished TV download records its own recovery-case kind, not one
/// folded into an existing kind, so Search for another and Stop wanting it
/// are offered rather than the ordinary Try again branches.
/// </summary>
public sealed class SeriesDispatchTitleRecoveryTests
{
    [Fact]
    public async Task A_tv_dispatch_records_a_vanished_downloading_case()
    {
        const string mediaType = "tv";
        var catalog = new Mock<ISeriesCatalogRepository>();
        var handler = new SeriesDispatchRecoveryHandler(catalog.Object);

        await handler.HandleVanishedDownloadingAsync("Breaking Bad", mediaType, "qb", "qBittorrent", "Breaking.Bad.S01E01", "e1", "{}", CancellationToken.None);

        catalog.Verify(
            repo => repo.AddImportRecoveryCaseAsync(It.Is<CreateSeriesImportRecoveryCaseRequest>(request => request.FailureKind == "vanished-downloading"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task A_movie_dispatch_is_left_to_the_movie_handler()
    {
        var catalog = new Mock<ISeriesCatalogRepository>();
        var handler = new SeriesDispatchRecoveryHandler(catalog.Object);

        await handler.HandleVanishedDownloadingAsync("Sintel", "movies", "qb", "qBittorrent", "Sintel.2010", "m1", "{}", CancellationToken.None);

        catalog.Verify(
            repo => repo.AddImportRecoveryCaseAsync(It.IsAny<CreateSeriesImportRecoveryCaseRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
