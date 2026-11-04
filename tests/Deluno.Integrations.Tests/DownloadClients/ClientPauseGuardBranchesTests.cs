using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>A pause or resume counts as done only when the client had the download before and reports it in the new state after.</summary>
public sealed class ClientPauseGuardBranchesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(1);
    private static readonly IntegrationFailureKind Rejected = IntegrationFailureFactory.FromLegacy("download-client", "c1", "qBit", "x", "rejected", "m").Kind;

    private static DownloadClientItem Client()
        => new("c1", "qBit", "qbittorrent", "localhost", 8080, null, null, null, false, null, "movies", "tv", null, 1, true, "healthy", null, null, null, Now, Now, Now);

    private static ClientPauseReading Yes(bool paused) => new(ClientHas.Yes, paused);

    private static async Task<(DownloadClientActionResult Result, int Sends, int Reads)> RunAsync(string action, bool sendOk, params ClientPauseReading[] readings)
    {
        var reads = 0;
        var sends = 0;
        var sent = new DownloadClientActionResult("c1", "hash", action, sendOk, sendOk ? "told" : "refused");
        var result = await ClientPauseGuard.RunAsync(Client(), "hash", action, "qBit",
            () => Task.FromResult(readings[Math.Min(reads++, readings.Length - 1)]),
            () => { sends++; return Task.FromResult(sent); },
            TimeSpan.FromMilliseconds(200), Fast);
        return (result, sends, reads);
    }

    [Theory]
    [InlineData("pause", "qBit has no download with that id, so nothing was paused. It may already have been removed there.")]
    [InlineData("PAUSE", "qBit has no download with that id, so nothing was paused. It may already have been removed there.")]
    [InlineData("resume", "qBit has no download with that id, so nothing was resumed. It may already have been removed there.")]
    public async Task A_client_without_the_download_changes_nothing(string action, string message)
    {
        var (result, sends, _) = await RunAsync(action, true, new ClientPauseReading(ClientHas.No, false));

        Assert.False(result.Succeeded);
        Assert.Equal(message, result.Message);
        Assert.Equal(Rejected, result.Failure!.Kind);
        Assert.Equal(0, sends);
    }

    [Fact]
    public async Task A_client_that_cannot_be_asked_changes_nothing_and_is_unreachable()
    {
        var (result, sends, _) = await RunAsync("pause", true, new ClientPauseReading(ClientHas.Unknown, false));

        Assert.Equal("qBit could not be asked about this download, so nothing was changed.", result.Message);
        Assert.NotEqual(Rejected, result.Failure!.Kind);
        Assert.Equal(0, sends);
    }

    [Theory]
    [InlineData("pause", true, "qBit already has this download paused.")]
    [InlineData("resume", false, "qBit already has this download running.")]
    public async Task A_download_already_in_the_wanted_state_is_a_success_without_a_command(string action, bool paused, string message)
    {
        var (result, sends, _) = await RunAsync(action, true, Yes(paused));

        Assert.True(result.Succeeded);
        Assert.Equal(message, result.Message);
        Assert.Equal(0, sends);
    }

    [Fact]
    public async Task A_command_the_client_refuses_is_returned_as_it_was_and_not_checked_again()
    {
        var (result, sends, reads) = await RunAsync("pause", false, Yes(false));

        Assert.False(result.Succeeded);
        Assert.Equal("refused", result.Message);
        Assert.Equal(1, sends);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task A_change_seen_after_a_short_wait_is_a_success()
    {
        var (result, _, reads) = await RunAsync("pause", true, Yes(false), Yes(false), Yes(false), Yes(true));

        Assert.True(result.Succeeded);
        Assert.Equal("told", result.Message);
        Assert.Equal(4, reads);
    }

    [Theory]
    [InlineData("pause", false, "qBit still has this download running after being told to pause it, so it was not paused.")]
    [InlineData("resume", true, "qBit still has this download paused after being told to resume it, so it was not resumed.")]
    public async Task A_change_never_seen_says_the_download_is_still_as_it_was(string action, bool startPaused, string message)
    {
        var (result, _, _) = await RunAsync(action, true, Yes(startPaused));

        Assert.False(result.Succeeded);
        Assert.Equal(message, result.Message);
        Assert.Equal(Rejected, result.Failure!.Kind);
    }

    [Fact]
    public async Task A_download_that_vanishes_after_the_command_is_not_called_paused()
    {
        var (result, _, _) = await RunAsync("pause", true, Yes(false), new ClientPauseReading(ClientHas.No, false));

        Assert.Equal("qBit no longer lists this download, so Deluno cannot say it was paused.", result.Message);
    }

    [Fact]
    public async Task A_client_that_stops_answering_after_the_command_is_not_believed()
    {
        var (result, _, _) = await RunAsync("resume", true, Yes(true), new ClientPauseReading(ClientHas.Unknown, false));

        Assert.Equal("qBit did not confirm the change, so Deluno cannot say the download was resumed.", result.Message);
        Assert.NotEqual(Rejected, result.Failure!.Kind);
    }

    [Fact]
    public async Task The_wait_for_a_change_ends_at_the_window_and_the_default_window_is_two_seconds()
    {
        var reads = 0;
        var result = await ClientPauseGuard.RunAsync(Client(), "hash", "pause", "qBit",
            () => { reads++; return Task.FromResult(Yes(false)); },
            () => Task.FromResult(new DownloadClientActionResult("c1", "hash", "pause", true, "told")),
            TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(2));

        Assert.False(result.Succeeded);
        Assert.True(reads >= 4);
        Assert.Equal(TimeSpan.FromSeconds(2), ClientPauseGuard.SettleWindow);
        Assert.Equal(TimeSpan.FromMilliseconds(250), ClientPauseGuard.SettlePoll);
    }

    [Fact]
    public async Task Without_its_own_timings_the_guard_uses_the_default_ones_and_still_confirms_at_once()
    {
        var reads = 0;
        var result = await ClientPauseGuard.RunAsync(Client(), "hash", "pause", "qBit",
            () => Task.FromResult(Yes(reads++ > 0)),
            () => Task.FromResult(new DownloadClientActionResult("c1", "hash", "pause", true, "told")));

        Assert.True(result.Succeeded);
    }
}
