using Deluno.Downloader.Engine;

namespace Deluno.Downloader.Tests.Engine;

public class JobLifecycleTransitionsTests
{
    [Theory]
    [InlineData(JobLifecycleState.Queued, JobLifecycleState.Fetching, DownloadProtocol.Nzb)]
    [InlineData(JobLifecycleState.Queued, JobLifecycleState.Fetching, DownloadProtocol.Torrent)]
    [InlineData(JobLifecycleState.Fetching, JobLifecycleState.Reassembled, DownloadProtocol.Nzb)]
    [InlineData(JobLifecycleState.Reassembled, JobLifecycleState.Verify, DownloadProtocol.Nzb)]
    [InlineData(JobLifecycleState.Verify, JobLifecycleState.Verified, DownloadProtocol.Nzb)]
    [InlineData(JobLifecycleState.Verified, JobLifecycleState.Extracting, DownloadProtocol.Nzb)]
    [InlineData(JobLifecycleState.Extracting, JobLifecycleState.Extracted, DownloadProtocol.Nzb)]
    [InlineData(JobLifecycleState.Extracted, JobLifecycleState.PostProcessed, DownloadProtocol.Nzb)]
    [InlineData(JobLifecycleState.PostProcessed, JobLifecycleState.ImportPending, DownloadProtocol.Nzb)]
    [InlineData(JobLifecycleState.ImportPending, JobLifecycleState.Done, DownloadProtocol.Nzb)]
    [InlineData(JobLifecycleState.Done, JobLifecycleState.Seeding, DownloadProtocol.Torrent)]
    [InlineData(JobLifecycleState.Seeding, JobLifecycleState.Done, DownloadProtocol.Torrent)]
    public void Happy_path_transitions_are_legal(JobLifecycleState from, JobLifecycleState to, DownloadProtocol p)
    {
        Assert.True(JobLifecycleTransitions.IsLegal(from, to, p));
    }

    [Theory]
    [InlineData(JobLifecycleState.Queued, JobLifecycleState.Done)]              // can't skip the whole pipeline
    [InlineData(JobLifecycleState.Fetching, JobLifecycleState.Extracting)]      // can't bypass verify
    [InlineData(JobLifecycleState.Done, JobLifecycleState.Queued)]              // Done is terminal except via Retry-from-Failed
    [InlineData(JobLifecycleState.Verify, JobLifecycleState.Repair)]            // repair is NZB-only — illegal for torrents
    public void Illegal_transitions_are_rejected(JobLifecycleState from, JobLifecycleState to)
    {
        // For torrent context where applicable; happy-path NZB+Repair is tested separately.
        Assert.False(JobLifecycleTransitions.IsLegal(from, to, DownloadProtocol.Torrent));
    }

    [Fact]
    public void Par2_repair_path_is_nzb_only()
    {
        Assert.True(JobLifecycleTransitions.IsLegal(JobLifecycleState.Verify, JobLifecycleState.Repair, DownloadProtocol.Nzb));
        Assert.True(JobLifecycleTransitions.IsLegal(JobLifecycleState.Repair, JobLifecycleState.Verified, DownloadProtocol.Nzb));
        Assert.False(JobLifecycleTransitions.IsLegal(JobLifecycleState.Verify, JobLifecycleState.Repair, DownloadProtocol.Torrent));
    }

    [Fact]
    public void Seeding_path_is_torrent_only()
    {
        Assert.True(JobLifecycleTransitions.IsLegal(JobLifecycleState.Done, JobLifecycleState.Seeding, DownloadProtocol.Torrent));
        Assert.False(JobLifecycleTransitions.IsLegal(JobLifecycleState.Done, JobLifecycleState.Seeding, DownloadProtocol.Nzb));
    }

    [Theory]
    [InlineData(JobLifecycleState.Queued)]
    [InlineData(JobLifecycleState.Fetching)]
    [InlineData(JobLifecycleState.Verify)]
    [InlineData(JobLifecycleState.Extracting)]
    [InlineData(JobLifecycleState.PostProcessed)]
    public void Any_non_terminal_state_can_go_to_Failed_or_Paused(JobLifecycleState from)
    {
        Assert.True(JobLifecycleTransitions.IsLegal(from, JobLifecycleState.Failed, DownloadProtocol.Nzb));
        Assert.True(JobLifecycleTransitions.IsLegal(from, JobLifecycleState.Paused, DownloadProtocol.Nzb));
    }

    [Fact]
    public void Retry_from_Failed_goes_back_to_Queued()
    {
        Assert.True(JobLifecycleTransitions.IsLegal(JobLifecycleState.Failed, JobLifecycleState.Queued, DownloadProtocol.Nzb));
    }

    [Theory]
    [InlineData(JobLifecycleState.Fetching)]
    [InlineData(JobLifecycleState.Reassembled)]
    [InlineData(JobLifecycleState.Verify)]
    [InlineData(JobLifecycleState.Verified)]
    [InlineData(JobLifecycleState.Repair)]
    [InlineData(JobLifecycleState.Extracting)]
    [InlineData(JobLifecycleState.Extracted)]
    [InlineData(JobLifecycleState.PostProcessed)]
    [InlineData(JobLifecycleState.ImportPending)]
    public void Crash_recovery_can_re_queue_any_mid_flight_state(JobLifecycleState from)
    {
        // The crash-recovery sweep at startup re-queues jobs the previous
        // process died mid-execution on. Every mid-flight state must
        // accept a transition back to Queued.
        Assert.True(JobLifecycleTransitions.IsLegal(from, JobLifecycleState.Queued, DownloadProtocol.Nzb));
    }

    [Fact]
    public void Crash_recovery_can_re_queue_Seeding_torrent()
    {
        Assert.True(JobLifecycleTransitions.IsLegal(JobLifecycleState.Seeding, JobLifecycleState.Queued, DownloadProtocol.Torrent));
    }

    [Fact]
    public void Done_cannot_be_re_queued()
    {
        // Done is terminal-by-design: re-downloading a finished job is a
        // new-job operation, not a restart.
        Assert.False(JobLifecycleTransitions.IsLegal(JobLifecycleState.Done, JobLifecycleState.Queued, DownloadProtocol.Nzb));
        Assert.False(JobLifecycleTransitions.IsLegal(JobLifecycleState.Done, JobLifecycleState.Queued, DownloadProtocol.Torrent));
    }

    [Fact]
    public void Resume_from_Paused_can_reach_active_states()
    {
        Assert.True(JobLifecycleTransitions.IsLegal(JobLifecycleState.Paused, JobLifecycleState.Fetching, DownloadProtocol.Nzb));
        Assert.True(JobLifecycleTransitions.IsLegal(JobLifecycleState.Paused, JobLifecycleState.Extracting, DownloadProtocol.Nzb));
    }

    [Fact]
    public void EnsureLegal_throws_on_illegal_transition()
    {
        Assert.Throws<InvalidOperationException>(() =>
            JobLifecycleTransitions.EnsureLegal(JobLifecycleState.Queued, JobLifecycleState.Done, DownloadProtocol.Nzb));
    }

    [Fact]
    public void IsTerminal_only_for_Done_and_Failed()
    {
        Assert.True(JobLifecycleTransitions.IsTerminal(JobLifecycleState.Done));
        Assert.True(JobLifecycleTransitions.IsTerminal(JobLifecycleState.Failed));
        Assert.False(JobLifecycleTransitions.IsTerminal(JobLifecycleState.Seeding));
        Assert.False(JobLifecycleTransitions.IsTerminal(JobLifecycleState.Paused));
        Assert.False(JobLifecycleTransitions.IsTerminal(JobLifecycleState.Queued));
    }
}
