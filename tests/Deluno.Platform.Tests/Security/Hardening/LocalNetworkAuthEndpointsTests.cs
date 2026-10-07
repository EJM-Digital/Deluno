using Deluno.Security.Contracts;
using Deluno.Security.Data;
using Deluno.Security.Hardening;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Deluno.Platform.Tests.Security.Hardening;

public sealed class LocalNetworkAuthEndpointsTests
{
    private sealed class FakeRepository : ILocalNetworkAuthRepository
    {
        public LocalNetworkAuthSettings Current { get; set; } = LocalNetworkAuthSettings.Off;
        public LocalNetworkAuthSettings? Saved { get; private set; }

        public Task<LocalNetworkAuthSettings> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Current);

        public Task SaveAsync(LocalNetworkAuthSettings settings, CancellationToken cancellationToken)
        {
            Saved = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCache : ILocalNetworkAuthSettingsCache
    {
        public bool Invalidated { get; private set; }

        public Task<LocalNetworkAuthSettings> GetAsync(CancellationToken cancellationToken) => Task.FromResult(LocalNetworkAuthSettings.Off);

        public void Invalidate() => Invalidated = true;
    }

    private sealed class FakeNotifier : ILocalNetworkAuthChangeNotifier
    {
        public List<bool> Notified { get; } = [];

        public Task NotifyChangedAsync(bool enabled, CancellationToken cancellationToken)
        {
            Notified.Add(enabled);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAllowedHosts : IAllowedHostsRepository
    {
        public Task<AllowedHostsSettings> GetAsync(CancellationToken cancellationToken) => Task.FromResult(AllowedHostsSettings.Off);

        public Task SaveAsync(AllowedHostsSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeResolver(System.Net.IPAddress? address) : ITrustedProxyClientAddressResolver
    {
        public Task<System.Net.IPAddress?> ResolveAsync(HttpContext context, CancellationToken cancellationToken) => Task.FromResult(address);
    }

    [Fact]
    public async Task An_IPv4_address_that_arrived_over_IPv6_is_shown_as_plain_IPv4()
    {
        var result = await LocalNetworkAuthEndpoints.GetOverviewAsync(
            new DefaultHttpContext(), new FakeRepository(), new FakeAllowedHosts(),
            new FakeResolver(System.Net.IPAddress.Parse("::ffff:192.168.1.20")), CancellationToken.None);

        var overview = Assert.IsType<Ok<LocalNetworkAuthOverview>>(result).Value!;
        Assert.Equal("192.168.1.20", overview.CurrentAddress);
        Assert.True(overview.CurrentAddressIsLocal);
    }

    [Fact]
    public async Task Invalid_entries_are_refused_and_nothing_is_saved()
    {
        var repository = new FakeRepository();
        var cache = new FakeCache();
        var notifier = new FakeNotifier();
        var context = new DefaultHttpContext();

        var result = await LocalNetworkAuthEndpoints.SaveAsync(
            new UpdateLocalNetworkAuthRequest(true, [], ["not-an-address"], []),
            context, repository, cache, notifier, CancellationToken.None);

        Assert.IsType<ProblemHttpResult>(result);
        Assert.Null(repository.Saved);
        Assert.False(cache.Invalidated);
        Assert.Empty(notifier.Notified);
    }

    [Fact]
    public async Task Valid_entries_save_and_notify_when_enabled_changes()
    {
        var repository = new FakeRepository();
        var cache = new FakeCache();
        var notifier = new FakeNotifier();
        var context = new DefaultHttpContext();

        var result = await LocalNetworkAuthEndpoints.SaveAsync(
            new UpdateLocalNetworkAuthRequest(true, [], ["192.168.1.20", "192.168.1.0/28"], ["10.0.0.5"]),
            context, repository, cache, notifier, CancellationToken.None);

        Assert.IsType<Ok<LocalNetworkAuthSettings>>(result);
        Assert.NotNull(repository.Saved);
        Assert.True(repository.Saved!.Enabled);
        Assert.Equal(2, repository.Saved.AllowList.Count);
        Assert.True(cache.Invalidated);
        Assert.Equal([true], notifier.Notified);
    }

    [Fact]
    public async Task Saving_without_changing_enabled_does_not_notify()
    {
        var repository = new FakeRepository { Current = new LocalNetworkAuthSettings(true, [], [], []) };
        var cache = new FakeCache();
        var notifier = new FakeNotifier();
        var context = new DefaultHttpContext();

        var result = await LocalNetworkAuthEndpoints.SaveAsync(
            new UpdateLocalNetworkAuthRequest(true, [], ["192.168.1.20"], []),
            context, repository, cache, notifier, CancellationToken.None);

        Assert.IsType<Ok<LocalNetworkAuthSettings>>(result);
        Assert.Empty(notifier.Notified);
    }
}
