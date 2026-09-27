namespace Deluno.Worker.Tests.Services;

/// <summary>
/// #901: a global internet outage pauses scheduled and RSS search everywhere,
/// but a local outage - a library's root gone, a download client down - must
/// keep pausing only what depends on it, through the mechanisms that already
/// do that (<c>ILibraryAvailabilityService</c>, a client's own health). This
/// reads the source the way <see cref="APausedLibraryTests.Search_planning_is_gated_on_the_same_answer"/>
/// already does, so a refactor that merges or drops either gate fails a test
/// rather than being found on a live rig.
/// </summary>
public sealed class AnOutageStopsScheduledAndRssSearchTests
{
    [Fact]
    public void Scheduled_and_rss_search_are_gated_on_a_fresh_connectivity_check()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Deluno.Worker", "Services", "DelunoHeartbeatWorker.cs"));

        var plans = source.IndexOf("var automationPlans = libraries", StringComparison.Ordinal);
        Assert.True(plans >= 0, "The worker no longer builds automation plans from the library list.");

        var rss = source.IndexOf("RunRssSyncPassAsync", plans, StringComparison.Ordinal);
        Assert.True(rss >= 0, "The worker no longer runs the RSS sync pass here.");

        var window = source[plans..rss];

        Assert.Contains("IConnectivityMonitor", window, StringComparison.Ordinal);
        Assert.Contains("connectivity.IsOutage", window, StringComparison.Ordinal);
        Assert.Contains("PlanLibrarySearchesAsync", window, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two gates must stay independent: the library-availability filter
    /// (<c>IsUsable</c>) still runs regardless of the internet, and the new
    /// connectivity check does not fold into or replace it - a local NAS
    /// outage and a global internet outage are different problems with
    /// different scopes (#901).
    /// </summary>
    [Fact]
    public void The_local_availability_gate_and_the_global_outage_gate_are_independent_checks()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Deluno.Worker", "Services", "DelunoHeartbeatWorker.cs"));

        var plans = source.IndexOf("var automationPlans = libraries", StringComparison.Ordinal);
        var rss = source.IndexOf("RunRssSyncPassAsync", plans, StringComparison.Ordinal);
        var window = source[plans..rss];

        // Both conditions appear, and the availability read happens before
        // the connectivity read: the library list is still filtered by
        // reachability first, and the outage check adds a second, separate
        // condition rather than substituting for it.
        var availabilityCheck = window.IndexOf("availability.IsUsable", StringComparison.Ordinal);
        var connectivityCheck = window.IndexOf("IConnectivityMonitor", StringComparison.Ordinal);

        Assert.True(availabilityCheck >= 0);
        Assert.True(connectivityCheck >= 0);
        Assert.True(availabilityCheck < connectivityCheck, "Library availability should still be resolved independently of the connectivity gate.");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Deluno.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
