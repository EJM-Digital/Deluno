using Deluno.Api.Updates;

namespace Deluno.Tray.Tests;

/// <summary>
/// #1367: a manual check that finds an update said nothing when automatic checking was off, because the balloon was skipped
/// on the assumption that Deluno downloads by itself. The rule is now shared (UpdatePolicy) by the download and the balloon.
/// </summary>
public sealed class AFoundUpdateIsAlwaysAnnouncedTests
{
    private static readonly TrayUpdate Found = TrayUpdate.Describe(true, UpdateStates.UpdateAvailable, "1.3.0", true, false);
    private static readonly TrayUpdate Ready = TrayUpdate.Describe(true, UpdateStates.ReadyToRestart, "1.3.0", true, true);

    private static AppSettings With(bool auto, string mode) => new() { AutoCheckUpdates = auto, UpdateMode = mode };

    [Fact]
    public void The_found_update_is_the_download_one() => Assert.Equal(UpdateMenuAction.Download, Found.Action);

    [Theory]
    [InlineData(UpdateModes.DownloadBackground)]
    [InlineData(UpdateModes.NotifyOnly)]
    public void With_automatic_checking_off_a_found_update_is_announced_in_any_mode(string mode)
    {
        var settings = With(false, mode);
        Assert.False(UpdatePolicy.DownloadsByItself(settings));
        Assert.True(UpdatePolicy.ShouldAnnounce(Found, settings));
    }

    [Fact]
    public void With_automatic_checking_on_and_download_mode_found_is_silent_and_ready_is_announced()
    {
        var settings = With(true, UpdateModes.DownloadBackground);
        Assert.True(UpdatePolicy.DownloadsByItself(settings));
        Assert.False(UpdatePolicy.ShouldAnnounce(Found, settings));
        Assert.True(UpdatePolicy.ShouldAnnounce(Ready, settings));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void In_notify_only_mode_a_found_update_is_announced_whether_automatic_checking_is_on_or_off(bool auto)
    {
        var settings = With(auto, UpdateModes.NotifyOnly);
        Assert.False(UpdatePolicy.DownloadsByItself(settings));
        Assert.True(UpdatePolicy.ShouldAnnounce(Found, settings));
    }

    [Fact]
    public void Nothing_found_is_never_announced() => Assert.False(UpdatePolicy.ShouldAnnounce(TrayUpdate.None, With(false, UpdateModes.NotifyOnly)));
}
