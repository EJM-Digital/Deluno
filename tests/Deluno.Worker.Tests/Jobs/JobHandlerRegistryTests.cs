using Deluno.Jobs.Contracts;
using Deluno.Worker.Jobs;

namespace Deluno.Worker.Tests.Jobs;

public sealed class JobHandlerRegistryTests
{
    private sealed class FakeHandler(string jobType) : IJobHandler
    {
        public string JobType => jobType;

        public Task<string> HandleAsync(JobQueueItem job, CancellationToken cancellationToken)
            => Task.FromResult("ok");
    }

    [Fact]
    public void Resolve_returns_the_handler_registered_for_the_job_type()
    {
        var registry = new JobHandlerRegistry([new FakeHandler("movies.catalog.refresh"), new FakeHandler("series.catalog.refresh")]);

        var handler = registry.Resolve("movies.catalog.refresh");

        Assert.Equal("movies.catalog.refresh", handler.JobType);
    }

    [Fact]
    public void Resolve_is_case_insensitive()
    {
        var registry = new JobHandlerRegistry([new FakeHandler("movies.catalog.refresh")]);

        var handler = registry.Resolve("MOVIES.CATALOG.REFRESH");

        Assert.Equal("movies.catalog.refresh", handler.JobType);
    }

    [Fact]
    public void Resolve_throws_for_an_unregistered_job_type()
    {
        var registry = new JobHandlerRegistry([new FakeHandler("movies.catalog.refresh")]);

        var exception = Assert.Throws<InvalidOperationException>(() => registry.Resolve("episode.search"));
        Assert.Contains("episode.search", exception.Message);
    }

    [Fact]
    public void RegisteredJobTypes_lists_every_handler()
    {
        var registry = new JobHandlerRegistry([new FakeHandler("movies.catalog.refresh"), new FakeHandler("series.catalog.refresh")]);

        Assert.Equal(2, registry.RegisteredJobTypes.Count);
        Assert.Contains("movies.catalog.refresh", registry.RegisteredJobTypes);
        Assert.Contains("series.catalog.refresh", registry.RegisteredJobTypes);
    }
}
