using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1414: a SABnzbd job a person paused reads Paused (so Resume is offered), a whole queue paused reads Paused,
/// and Queued stays for a job that is only waiting. Every queue here is a recorded SABnzbd 5.1.3 reply.
/// </summary>
public sealed class ASabnzbdJobAPersonPausedReadsPausedTests
{
    private static async Task<IReadOnlyList<DownloadQueueItem>> QueueOfAsync(string state, params (string Path, object? Value)[] edits)
    {
        var queue = RecordedReplies.Sabnzbd(state).Body("mode=queue&output=json&apikey", edits);
        var history = RecordedReplies.Sabnzbd("idle").Body("mode=history&output");
        var version = RecordedReplies.Sabnzbd("version").Body("mode=version");
        var handler = new Routed(queue, history, version);
        var client = new DownloadClientItem("c", "SABnzbd", "sabnzbd", "localhost", 8080, null, "api-key", "http://localhost:8080/", false, null,
            "movies", "tv", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var snapshot = await new SabnzbdDownloadClient(new Factory(handler)).GetSnapshotAsync(client, DateTimeOffset.UtcNow, CancellationToken.None);
        return snapshot!.Queue;
    }

    private static string StatusOf(IReadOnlyList<DownloadQueueItem> queue, string title) => queue.Single(item => item.Id.StartsWith(title)).Status;

    [Fact]
    public async Task A_job_paused_by_a_person_reads_paused()
    {
        var queue = await QueueOfAsync("paused");
        Assert.Equal(DownloadQueueStatuses.Paused, StatusOf(queue, "7690dfab"));
    }

    [Fact]
    public async Task A_job_that_is_only_waiting_reads_queued()
    {
        // The recorded queued job sat in a paused queue; the same recorded slot with the queue running is only waiting.
        var queue = await QueueOfAsync("queued", ("queue.paused", false));
        Assert.Contains(queue, item => item.Status == DownloadQueueStatuses.Queued);
        Assert.DoesNotContain(queue, item => item.Status == DownloadQueueStatuses.Paused);
    }

    [Fact]
    public async Task Every_job_in_a_queue_paused_as_a_whole_reads_paused()
    {
        var queue = await QueueOfAsync("queued");
        Assert.NotEmpty(queue);
        Assert.All(queue, item => Assert.Equal(DownloadQueueStatuses.Paused, item.Status));
    }

    [Fact]
    public async Task A_job_downloading_in_a_running_queue_reads_downloading()
    {
        var queue = await QueueOfAsync("downloading");
        Assert.Equal(DownloadQueueStatuses.Downloading, StatusOf(queue, "7690dfab"));
    }

    [Fact]
    public void An_NZBGet_job_paused_by_a_person_reads_paused_on_its_recorded_status()
    {
        var listgroups = JsonNode.Parse(RecordedReplies.Nzbget("files-of-a-paused-job").Body("\"listgroups\""))!["result"]!.AsArray();
        var status = listgroups.First(group => group!["Status"]!.GetValue<string>() == "PAUSED")!["Status"]!.GetValue<string>();

        Assert.Equal(DownloadQueueStatuses.Paused, new NzbGetDownloadClient(new Factory(new Routed("{}", "{}", "{}"))).NormalizeStatus(status, 10));
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Routed(string queue, string history, string version) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = request.RequestUri?.Query ?? string.Empty;
            var body = query.Contains("mode=history", StringComparison.OrdinalIgnoreCase) ? history
                : query.Contains("mode=version", StringComparison.OrdinalIgnoreCase) ? version
                : queue;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
