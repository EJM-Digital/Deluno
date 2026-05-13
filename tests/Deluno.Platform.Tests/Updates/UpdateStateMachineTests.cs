using Deluno.Api.Updates;

namespace Deluno.Platform.Tests.Updates;

public sealed class UpdateStateMachineTests
{
    [Fact]
    public void MarkChecking_sets_checking_and_clears_error()
    {
        var state = new UpdateStateMachine();
        state.MarkError("boom");

        state.MarkChecking();

        Assert.Equal(UpdateStates.Checking, state.State);
        Assert.Null(state.LastError);
        Assert.Null(state.ProgressPercent);
    }

    [Fact]
    public void MarkUpdateAvailable_sets_latest_version_and_state()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new UpdateStateMachine();

        state.MarkUpdateAvailable(now, "1.2.3");

        Assert.Equal(UpdateStates.UpdateAvailable, state.State);
        Assert.True(state.UpdateAvailable);
        Assert.False(state.RestartRequired);
        Assert.Equal("1.2.3", state.LatestVersion);
        Assert.Equal(now, state.LastCheckedUtc);
    }

    [Fact]
    public void Download_flow_reaches_ready_to_restart()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new UpdateStateMachine();

        state.MarkDownloading();
        state.ReportProgress(45);
        state.MarkDownloaded(now, restartRequired: true);

        Assert.Equal(UpdateStates.ReadyToRestart, state.State);
        Assert.True(state.UpdateAvailable);
        Assert.True(state.RestartRequired);
        Assert.Equal(100, state.ProgressPercent);
        Assert.Equal(now, state.LastDownloadedUtc);
    }

    [Fact]
    public void MarkUpToDate_respects_restart_required_state()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new UpdateStateMachine();

        state.MarkUpToDate(now, restartRequired: false);
        Assert.Equal(UpdateStates.UpToDate, state.State);
        Assert.False(state.RestartRequired);
        Assert.False(state.UpdateAvailable);

        state.MarkUpToDate(now, restartRequired: true);
        Assert.Equal(UpdateStates.ReadyToRestart, state.State);
        Assert.True(state.RestartRequired);
        Assert.True(state.UpdateAvailable);
    }

    [Fact]
    public void MarkError_sets_error_state_and_message()
    {
        var state = new UpdateStateMachine();

        state.MarkError("update failed");

        Assert.Equal(UpdateStates.Error, state.State);
        Assert.Equal("update failed", state.LastError);
    }
}
