using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1356: NZBGet 26.3 reports a job with 380 KB left as <c>RemainingSizeMB: 0</c> (recorded: <c>stalled-near-end</c>), so Deluno's check
/// "everything left is paused" needed <c>RemainingSizeMB &gt; 0</c> and never held: Pause on a nearly finished job said "did not pause"
/// although NZBGet had paused it. NZBGet also gives the byte sizes (<c>RemainingSizeLo/Hi</c>, <c>PausedSizeLo/Hi</c>); the check uses them.
/// The list reply is the recorded one of that job, changed only in what a paused job shows: the paused bytes equal the remaining bytes.
/// </summary>
public sealed class NzbGetPausesANearlyDoneJobTests
{
    private const string NearlyDoneId = "29";

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static Task<DownloadClientActionResult> Pause(bool nzbGetObeys)
    {
        var paused = false;
        var groups = RecordedReplies.Nzbget("stalled-near-end");
        var handler = RecordedReplies.Handler("nzbget", [RecordedReplies.Nzbget("paused")], (request, body) =>
        {
            if (body.Contains("GroupPause", StringComparison.Ordinal))
            {
                paused = nzbGetObeys;
                return RecordedReplies.Nzbget("paused").Find("GroupPause").Reply.ToResponse();
            }

            return body.Contains("\"listgroups\"", StringComparison.Ordinal)
                ? RecordedReplies.Json(paused ? groups.Body("listgroups", ("result.1.PausedSizeLo", 380512)) : groups.Body("listgroups"))
                : null;
        });
        return new NzbGetDownloadClient(new Factory(handler), removalSettle: TimeSpan.Zero)
            .ExecuteActionAsync(EveryClientPausesAndResumesOnlyWhatItHasTests.Client("nzbget"), DownloadClientActions.Pause, NearlyDoneId, CancellationToken.None);
    }

    [Fact]
    public async Task Pausing_a_job_with_under_a_megabyte_left_is_done_once_NZBGet_shows_all_of_what_is_left_paused()
    {
        var recorded = RecordedReplies.Nzbget("stalled-near-end").Find("listgroups").Reply.Json()["result"]![1]!;
        Assert.Equal(0, (int)recorded["RemainingSizeMB"]!);
        Assert.Equal(380512, (long)recorded["RemainingSizeLo"]!);

        var result = await Pause(nzbGetObeys: true);

        Assert.True(result.Succeeded, result.Message);
    }

    [Fact]
    public async Task Pausing_a_job_with_under_a_megabyte_left_still_fails_when_NZBGet_did_not_pause_it()
    {
        var result = await Pause(nzbGetObeys: false);

        Assert.False(result.Succeeded);
    }
}
