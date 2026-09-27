using Deluno.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Deluno.Persistence.Tests.Realtime;

/// <summary>
/// A throttled background tab's own keep-alive ping can be delayed past the
/// framework's 30 s default <see cref="HubOptions.ClientTimeoutInterval"/>,
/// which drops the connection and forces a reconnect-and-resync: the
/// catch-up #937 exists to remove. These assert the module actually sets
/// the tolerant timeouts <see cref="RealtimeServiceCollectionExtensions"/>
/// documents, so a future edit that reverts to the bare
/// <c>services.AddSignalR()</c> default fails a test instead of silently
/// reintroducing the drop.
/// </summary>
public sealed class RealtimeHubOptionsTests
{
    [Fact]
    public void AddDelunoRealtimeModule_sets_a_client_timeout_that_tolerates_one_throttled_minute()
    {
        var services = new ServiceCollection();
        services.AddDelunoRealtimeModule();
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<HubOptions>>().Value;

        // Chrome throttles a hidden/occluded/minimised tab's chained timers
        // to at most one firing a minute; this must clear that with room to
        // spare or a throttled tab gets disconnected for no real outage.
        Assert.True(options.ClientTimeoutInterval >= TimeSpan.FromSeconds(60));
        Assert.Equal(TimeSpan.FromSeconds(90), options.ClientTimeoutInterval);
    }

    [Fact]
    public void AddDelunoRealtimeModule_keeps_server_keep_alive_well_under_the_client_timeout()
    {
        var services = new ServiceCollection();
        services.AddDelunoRealtimeModule();
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<HubOptions>>().Value;

        // SignalR's own guidance: KeepAliveInterval should be well below
        // ClientTimeoutInterval so more than one ping can be missed before
        // the client is dropped as unresponsive.
        Assert.True(options.KeepAliveInterval * 2 < options.ClientTimeoutInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), options.KeepAliveInterval);
    }
}
