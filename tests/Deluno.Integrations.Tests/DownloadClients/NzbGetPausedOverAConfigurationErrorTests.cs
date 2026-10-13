using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1257 and #1237, from NZBGet 26.3's real replies: after a reload that finds an invalid line in nzbget.conf, NZBGet logs "Invalid
/// option" and pauses all activity (its status says DownloadPaused, PostPaused and ScanPaused are true). With the line removed and a
/// reload, the same switches read false with nothing else to do. Deluno reads the client's own pause flags, so the first shows the
/// client paused and the second does not.
/// </summary>
public sealed class NzbGetPausedOverAConfigurationErrorTests
{
    private static DownloadClientItem Client()
        => new("nzb-1", "NZBGet", "nzbget", "localhost", 6789, "nzbget", "tegbzn6789", "http://localhost:6789/", false, null,
            "Movies", "TV", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static async Task<DownloadClientTelemetrySnapshot> Snapshot(string statusRecording)
    {
        // The status call is the recording's; the lists, version and settings are NZBGet's other recorded answers.
        var handler = RecordedReplies.Handler(
            "nzbget",
            [RecordedReplies.Nzbget(statusRecording), RecordedReplies.Nzbget("idle"), RecordedReplies.Nzbget("version"), RecordedReplies.Nzbget("settings")]);
        return (await new NzbGetDownloadClient(new Factory(handler)).GetSnapshotAsync(Client(), DateTimeOffset.UtcNow, CancellationToken.None))!;
    }

    [Fact]
    public async Task A_client_paused_over_a_configuration_error_is_shown_paused()
    {
        var snapshot = await Snapshot("config-error-paused");

        Assert.True(snapshot.ClientPaused);
    }

    [Fact]
    public async Task The_same_client_after_the_bad_line_is_gone_and_it_reloaded_is_not_shown_paused()
    {
        var snapshot = await Snapshot("config-error-cleared");

        Assert.False(snapshot.ClientPaused);
    }

    /// <summary>The recorded log says why NZBGet paused; the words are NZBGet's, and the status alone is what Deluno acts on.</summary>
    [Fact]
    public void The_recorded_log_names_the_bad_option_and_the_pause()
    {
        var log = RecordedReplies.Nzbget("config-error-paused").Find("\"log\"").Reply.Body!;

        Assert.Contains("Invalid option", log, StringComparison.Ordinal);
        Assert.Contains("AppBin", log, StringComparison.Ordinal);
        Assert.Contains("Pausing all activities due to errors in configuration", log, StringComparison.Ordinal);
    }
}
