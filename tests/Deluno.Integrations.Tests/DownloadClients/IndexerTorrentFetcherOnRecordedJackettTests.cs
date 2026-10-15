using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1301: Deluno following a torrent release's download link itself (#1062), against what Jackett 0.24.2850 answered when asked
/// for something it does not have: a tracker that is not set up (HTTP 500 with a JSON error) and a function it does not know
/// (HTTP 200 with an XML error element where a torrent was expected). Neither is a bad release, and neither is a torrent.
/// </summary>
public sealed class IndexerTorrentFetcherOnRecordedJackettTests
{
    private static IndexerItem Indexer()
    {
        var now = DateTimeOffset.UnixEpoch;
        return new IndexerItem("ix-1", "Jackett YTS", "torznab", "public", "http://ix.invalid", null, 100, "", "", "both", true, "healthy", null, null, null, null, 0, null, null, now, now);
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Answering(RecordedReply reply, List<string> asked) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            asked.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(reply.ToResponse());
        }
    }

    [Fact]
    public async Task A_link_Jackett_answers_with_a_server_error_is_a_problem_with_the_indexer_and_never_a_dead_release()
    {
        var reply = RecordedReplies.Jackett("unknown-indexer").Single.Reply;
        Assert.Equal(500, reply.Status);
        var asked = new List<string>();

        var fetched = await new IndexerTorrentFetcher(new Factory(new Answering(reply, asked))).FetchAsync(Indexer(), "http://ix.invalid/dl/yts/?path=abc", CancellationToken.None);

        Assert.False(fetched.Succeeded);
        Assert.False(fetched.ReleaseIsGone);
        Assert.False(fetched.AuthFailed);
        Assert.False(string.IsNullOrWhiteSpace(fetched.Problem));
        Assert.Equal("http://ix.invalid/dl/yts/?path=abc", Assert.Single(asked));
    }

    [Fact]
    public async Task An_xml_error_Jackett_answers_a_link_with_is_not_taken_for_a_torrent_and_is_not_a_dead_release()
    {
        var reply = RecordedReplies.Jackett("unknown-function").Single.Reply;
        Assert.Equal(200, reply.Status);

        var fetched = await new IndexerTorrentFetcher(new Factory(new Answering(reply, []))).FetchAsync(Indexer(), "http://ix.invalid/dl/yts/?path=abc", CancellationToken.None);

        Assert.False(fetched.Succeeded);
        Assert.False(fetched.ReleaseIsGone);
        Assert.Null(fetched.TorrentFile);
        Assert.False(string.IsNullOrWhiteSpace(fetched.Problem));
    }
}
