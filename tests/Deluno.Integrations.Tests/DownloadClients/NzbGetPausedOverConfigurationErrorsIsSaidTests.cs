using System.Text.Json;
using System.Text.Json.Nodes;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1257, from NZBGet 26.3's real replies: with one invalid line in nzbget.conf, a reload makes NZBGet log "Invalid option"
/// and "Pausing all activities due to errors in configuration", and its status then says DownloadPaused. Nothing in Deluno
/// said why, so every download sat at 0 bytes. A pause the person chose has the same status and no such log entry, so only
/// the log tells the two apart, and it is read only when the status says downloads are paused.
/// </summary>
public sealed class NzbGetPausedOverConfigurationErrorsIsSaidTests
{
    private const string PlainSentence =
        "NZBGet paused all downloads because its settings have errors: nzbget.conf(1774): Invalid option \"AppBin\". Fix them in NZBGet's settings, then resume.";

    private static DownloadClientItem NzbGetClient()
        => new("nzb-1", "NZBGet", "nzbget", "localhost", 6789, "nzbget", "tegbzn6789", "http://localhost:6789/", false, null,
            "Movies", "TV", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>NZBGet as recorded: the status and log of the given state, the idle queue, the version and the settings.</summary>
    private static async Task<(Deluno.Integrations.DownloadClients.DownloadClientTelemetrySnapshot Snapshot, List<string> Methods)> Poll(string state, Func<Recording, string?>? logReply = null)
    {
        var stateRecording = RecordedReplies.Nzbget(state);
        var methods = new List<string>();
        var handler = RecordedReplies.Handler("nzbget",
            [stateRecording, RecordedReplies.Nzbget("idle"), RecordedReplies.Nzbget("version"), RecordedReplies.Nzbget("settings")],
            (_, body) =>
            {
                using var document = JsonDocument.Parse(body);
                var method = document.RootElement.GetProperty("method").GetString()!;
                methods.Add(method);
                return method == "log" && logReply?.Invoke(stateRecording) is { } own ? RecordedReplies.Json(own) : null;
            });
        var snapshot = await new NzbGetDownloadClient(new Factory(handler), TimeSpan.Zero)
            .GetSnapshotAsync(NzbGetClient(), DateTimeOffset.UtcNow, CancellationToken.None);
        return (snapshot!, methods);
    }

    [Fact]
    public async Task A_pause_over_errors_in_the_settings_is_said_in_plain_words_on_the_clients_health()
    {
        var (snapshot, _) = await Poll("config-error-paused");

        Assert.Equal("degraded", snapshot.HealthStatus);
        Assert.Equal(PlainSentence, snapshot.LastHealthMessage);
        Assert.True(snapshot.ClientPaused);
    }

    [Fact]
    public async Task The_problem_is_a_client_warning_so_it_is_counted_and_can_be_cleared_from_the_Connections_screen()
    {
        var (snapshot, _) = await Poll("config-error-paused");

        Assert.Equal([PlainSentence], snapshot.ClientWarnings);
    }

    [Fact]
    public async Task A_pause_the_person_chose_is_not_an_error()
    {
        // The recorded status (paused) with the log as NZBGet answers it when nothing went wrong: no configuration entry.
        var (snapshot, methods) = await Poll("config-error-paused", recording => recording.Body("\"log\"", ("result", new JsonArray())));

        Assert.Contains("log", methods);
        Assert.Equal("healthy", snapshot.HealthStatus);
        Assert.DoesNotContain("settings have errors", snapshot.LastHealthMessage);
        Assert.Null(snapshot.ClientWarnings);
        Assert.True(snapshot.ClientPaused);
    }

    [Fact]
    public async Task Once_the_errors_are_fixed_the_warning_goes_and_the_log_is_not_read_while_nothing_is_paused()
    {
        var (snapshot, methods) = await Poll("config-error-cleared");

        Assert.DoesNotContain("log", methods);
        Assert.Equal("healthy", snapshot.HealthStatus);
        Assert.Null(snapshot.ClientWarnings);
        Assert.False(snapshot.ClientPaused);
    }

    [Fact]
    public async Task Clearing_the_warning_resumes_NZBGet_and_is_what_the_clear_button_asks_for()
    {
        var methods = new List<string>();
        // No resumedownload reply is recorded yet; NZBGet answers it with a bare true, as it does a reload, so that recorded reply stands in.
        var trueReply = RecordedReplies.Nzbget("config-error-paused").Find("reload").Reply;
        var handler = RecordedReplies.Handler("nzbget", [], (_, body) =>
        {
            using var document = JsonDocument.Parse(body);
            methods.Add(document.RootElement.GetProperty("method").GetString()!);
            return trueReply.ToResponse();
        });

        var cleared = await new NzbGetDownloadClient(new Factory(handler), TimeSpan.Zero).ClearWarningsAsync(NzbGetClient(), CancellationToken.None);

        Assert.True(cleared);
        Assert.Equal(["resumedownload", "resumepost", "resumescan"], methods);
    }
}
