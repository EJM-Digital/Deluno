using Deluno.Integrations.Search;

namespace Deluno.Integrations.Tests.Search;

/// <summary>
/// #598: indexers are paced per service, host and port. Two services on one
/// machine do not wait for each other; two entries for the same service do.
/// </summary>
public sealed class IndexersArePacedPerServiceTests
{
    [Fact]
    public void Two_services_on_one_machine_are_paced_apart()
        => Assert.NotEqual(
            FeedMediaSearchPlanner.ThrottleKey(new Uri("http://127.0.0.1:9117/api?t=tvsearch")),
            FeedMediaSearchPlanner.ThrottleKey(new Uri("http://127.0.0.1:9118/api?t=tvsearch")));

    [Fact]
    public void Two_entries_for_one_service_share_its_pace()
        => Assert.Equal(
            FeedMediaSearchPlanner.ThrottleKey(new Uri("https://Tracker.Example/api/v2.0/indexers/a/results/torznab")),
            FeedMediaSearchPlanner.ThrottleKey(new Uri("https://tracker.example:443/api/v2.0/indexers/b/results/torznab")));
}
