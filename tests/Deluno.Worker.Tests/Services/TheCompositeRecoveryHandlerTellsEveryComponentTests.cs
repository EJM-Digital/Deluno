using Deluno.Contracts;
using Deluno.Recovery.Services;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// The one recovery handler hands every dispatch event to both the film and the show component, and adds up the cases
/// a settled scope closes (coverage register, area engines).
/// </summary>
public sealed class TheCompositeRecoveryHandlerTellsEveryComponentTests
{
    private static (CompositeDispatchRecoveryHandler Composite, Mock<IDispatchRecoveryHandlerComponent> Movies, Mock<IDispatchRecoveryHandlerComponent> Series) Build()
    {
        var movies = new Mock<IDispatchRecoveryHandlerComponent>();
        var series = new Mock<IDispatchRecoveryHandlerComponent>();
        return (new CompositeDispatchRecoveryHandler([movies.Object, series.Object]), movies, series);
    }

    [Fact]
    public async Task Every_dispatch_event_reaches_every_component_with_the_same_arguments()
    {
        var (composite, movies, series) = Build();
        var token = CancellationToken.None;

        await composite.HandleGrabTimeoutAsync("Dune", "movies", "client-1", "qBittorrent", "Dune.2021", "{}", token);
        await composite.HandleDetectionTimeoutAsync("Dune", "movies", "client-1", "qBittorrent", "Dune.2021", "movie-1", "{}", token);
        await composite.HandleImportTimeoutAsync("Dune", "movies", "client-1", "qBittorrent", "Dune.2021", "movie-1", "{}", token);
        await composite.HandleImportFailureAsync("Dune", "movies", "client-1", "qBittorrent", "Dune.2021", "movie-1", "unmatched", "No match.", "{}", token);
        await composite.HandleVanishedDownloadingAsync("Dune", "movies", "client-1", "qBittorrent", "Dune.2021", "movie-1", "{}", token);
        await composite.HandleDispatchSettledAsync("dispatch-1", "removed", token);

        foreach (var component in new[] { movies, series })
        {
            component.Verify(c => c.HandleGrabTimeoutAsync("Dune", "movies", "client-1", "qBittorrent", "Dune.2021", "{}", token), Times.Once);
            component.Verify(c => c.HandleDetectionTimeoutAsync("Dune", "movies", "client-1", "qBittorrent", "Dune.2021", "movie-1", "{}", token), Times.Once);
            component.Verify(c => c.HandleImportTimeoutAsync("Dune", "movies", "client-1", "qBittorrent", "Dune.2021", "movie-1", "{}", token), Times.Once);
            component.Verify(c => c.HandleImportFailureAsync("Dune", "movies", "client-1", "qBittorrent", "Dune.2021", "movie-1", "unmatched", "No match.", "{}", token), Times.Once);
            component.Verify(c => c.HandleVanishedDownloadingAsync("Dune", "movies", "client-1", "qBittorrent", "Dune.2021", "movie-1", "{}", token), Times.Once);
            component.Verify(c => c.HandleDispatchSettledAsync("dispatch-1", "removed", token), Times.Once);
        }
    }

    [Fact]
    public async Task The_cases_a_settled_scope_closes_are_the_sum_across_components()
    {
        var (composite, movies, series) = Build();
        var scope = new ImportRecoveryCaseScope(["dispatch-1"], ["Dune.2021"]);
        movies.Setup(c => c.HandleScopeSettledAsync(scope, "removed", It.IsAny<CancellationToken>())).ReturnsAsync(2);
        series.Setup(c => c.HandleScopeSettledAsync(scope, "removed", It.IsAny<CancellationToken>())).ReturnsAsync(3);

        Assert.Equal(5, await composite.HandleScopeSettledAsync(scope, "removed", CancellationToken.None));
    }
}
