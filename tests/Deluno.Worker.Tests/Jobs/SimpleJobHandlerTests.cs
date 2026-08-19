using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;

namespace Deluno.Worker.Tests.Jobs;

public sealed class SimpleJobHandlerTests
{
    [Fact]
    public async Task MoviesCatalogRefreshJobHandler_returns_the_fixed_completion_message()
    {
        var handler = new MoviesCatalogRefreshJobHandler();

        var message = await handler.HandleAsync(TestJobs.Create("movies.catalog.refresh"), CancellationToken.None);

        Assert.Equal("Finished checking your movie library.", message);
    }

    [Fact]
    public async Task SeriesCatalogRefreshJobHandler_returns_the_fixed_completion_message()
    {
        var handler = new SeriesCatalogRefreshJobHandler();

        var message = await handler.HandleAsync(TestJobs.Create("series.catalog.refresh"), CancellationToken.None);

        Assert.Equal("Finished checking your TV show library.", message);
    }
}
