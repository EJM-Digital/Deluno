using Deluno.Security.Contracts;
using Deluno.Security.Data;
using Deluno.Security.Hardening;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Deluno.Platform.Tests.Security.Hardening;

public sealed class AllowedHostsEndpointsTests
{
    private sealed class FakeRepository : IAllowedHostsRepository
    {
        public AllowedHostsSettings? Saved { get; private set; }

        public Task<AllowedHostsSettings> GetAsync(CancellationToken cancellationToken) => Task.FromResult(AllowedHostsSettings.Off);

        public Task SaveAsync(AllowedHostsSettings settings, CancellationToken cancellationToken)
        {
            Saved = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCache : IAllowedHostsSettingsCache
    {
        public bool Invalidated { get; private set; }

        public Task<AllowedHostsSettings> GetAsync(CancellationToken cancellationToken) => Task.FromResult(AllowedHostsSettings.Off);

        public void Invalidate() => Invalidated = true;
    }

    private static DefaultHttpContext HttpContextFor(string host)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);
        return context;
    }

    [Fact]
    public async Task Turning_it_on_without_the_current_host_on_the_list_is_refused()
    {
        var repository = new FakeRepository();
        var cache = new FakeCache();
        var context = HttpContextFor("my-pc:5099");

        var result = await AllowedHostsEndpoints.SaveAsync(
            new UpdateAllowedHostsRequest(true, ["some-other-host"]),
            context, repository, cache, CancellationToken.None);

        Assert.IsType<ProblemHttpResult>(result);
        Assert.Null(repository.Saved);
        Assert.False(cache.Invalidated);
    }

    [Fact]
    public async Task Turning_it_on_with_the_current_host_on_the_list_saves()
    {
        var repository = new FakeRepository();
        var cache = new FakeCache();
        var context = HttpContextFor("my-pc:5099");

        var result = await AllowedHostsEndpoints.SaveAsync(
            new UpdateAllowedHostsRequest(true, ["my-pc"]),
            context, repository, cache, CancellationToken.None);

        Assert.IsType<Ok<AllowedHostsSettings>>(result);
        Assert.NotNull(repository.Saved);
        Assert.True(repository.Saved!.Enabled);
        Assert.True(cache.Invalidated);
    }

    [Fact]
    public async Task Turning_it_on_from_localhost_never_needs_localhost_on_the_list()
    {
        var repository = new FakeRepository();
        var cache = new FakeCache();
        var context = HttpContextFor("localhost:5099");

        var result = await AllowedHostsEndpoints.SaveAsync(
            new UpdateAllowedHostsRequest(true, ["my-pc"]),
            context, repository, cache, CancellationToken.None);

        Assert.IsType<Ok<AllowedHostsSettings>>(result);
    }

    [Fact]
    public async Task Turning_it_off_never_needs_the_current_host_on_the_list()
    {
        var repository = new FakeRepository();
        var cache = new FakeCache();
        var context = HttpContextFor("evil.example.com");

        var result = await AllowedHostsEndpoints.SaveAsync(
            new UpdateAllowedHostsRequest(false, []),
            context, repository, cache, CancellationToken.None);

        Assert.IsType<Ok<AllowedHostsSettings>>(result);
    }
}
