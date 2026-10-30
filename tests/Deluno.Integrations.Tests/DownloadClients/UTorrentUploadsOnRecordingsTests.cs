using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1434: uTorrent 3.6 cannot read a multipart upload whose boundary is quoted (what .NET writes by default). It answers HTTP 200
/// with an error in the body and adds nothing; Deluno read the 200 as a send. The replies are the real ones (the quoted-boundary
/// upload, the accepted upload, and a file that is not a torrent); the stand-in answers each by what the request looked like.
/// </summary>
public sealed class UTorrentUploadsOnRecordingsTests
{
    private static readonly byte[] TorrentFile = Encoding.Latin1.GetBytes("d4:infod6:lengthi5eee");

    [Fact]
    public async Task An_upload_uTorrent_accepts_is_followed()
    {
        var uTorrent = new UTorrent();

        var result = await Grab(uTorrent);

        Assert.True(result.Succeeded, result.Message);
        Assert.NotNull(result.ExternalId);
    }

    [Fact]
    public async Task The_upload_is_sent_with_a_boundary_that_is_not_quoted()
    {
        var uTorrent = new UTorrent();

        await Grab(uTorrent);

        Assert.NotNull(uTorrent.UploadContentType);
        Assert.DoesNotContain("boundary=\"", uTorrent.UploadContentType, StringComparison.Ordinal);
        Assert.Contains("boundary=", uTorrent.UploadContentType, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_200_that_carries_uTorrents_error_is_a_failed_send_in_plain_words()
    {
        var uTorrent = new UTorrent { AlwaysRefuse = true };

        var result = await Grab(uTorrent);

        Assert.False(result.Succeeded);
        Assert.Contains("uTorrent did not take the torrent file", result.Message, StringComparison.Ordinal);
        Assert.Contains("torrent file content not supplied in form parameter", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_uTorrent_says_is_not_a_torrent_is_a_failed_send_too()
    {
        var uTorrent = new UTorrent { NotATorrent = true };

        var result = await Grab(uTorrent);

        Assert.False(result.Succeeded);
        Assert.Contains("Can't add torrent", result.Message, StringComparison.Ordinal);
    }

    private static Task<DownloadClientGrabResult> Grab(UTorrent uTorrent)
        => new UTorrentDownloadClient(() => uTorrent).GrabAsync(
            new("utorrent-1", "uTorrent", "utorrent", "localhost", 8081, null, null, "http://localhost:8081/", false, null, "movies", "tv", null, 1, true,
                "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new DownloadClientGrabRequest("The.Show.S01E01.2023.720p.WEB-DL", "http://indexer.test/download/1", "tv", null, "Lab Torznab", "dispatch-1")
            {
                TorrentFile = TorrentFile
            },
            CancellationToken.None);

    /// <summary>uTorrent as recorded: the upload answers by the boundary it was sent with, and the list shows the torrent once added.</summary>
    private sealed class UTorrent : HttpMessageHandler
    {
        private bool _added;
        public string? UploadContentType { get; private set; }
        public bool AlwaysRefuse { get; init; }
        public bool NotATorrent { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            if (uri.Contains("token.html", StringComparison.Ordinal))
            {
                var token = RecordedReplies.Utorrent("token").Single.Reply.Body!.Replace("<scrubbed>", "recorded-token");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(token, Encoding.UTF8) };
            }

            if (uri.Contains("action=add-file", StringComparison.Ordinal))
            {
                UploadContentType = request.Content!.Headers.ContentType!.ToString();
                _ = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                var quoted = UploadContentType.Contains("boundary=\"", StringComparison.Ordinal);
                if (NotATorrent) return Reply(RecordedReplies.Utorrent("add-refused").Single.Reply.Body!);
                if (quoted || AlwaysRefuse) return Reply(RecordedReplies.Utorrent("add-file-quoted-boundary").Exchanges[0].Reply.Body!);
                _added = true;
                return Reply(RecordedReplies.Utorrent("add-file-accepted").Exchanges[0].Reply.Body!);
            }

            if (uri.Contains("list=1", StringComparison.Ordinal))
            {
                var listed = _added ? RecordedReplies.Utorrent("add-file-accepted").Exchanges[1] : RecordedReplies.Utorrent("idle").Single;
                // The recorded torrent has the recorded hash; the client follows the hash of the file it sent, so answer with that one.
                var hash = Deluno.Integrations.DownloadClients.TorrentInfoHash.FromTorrentFile(TorrentFile, out _);
                var body = listed.Reply.Body!;
                if (_added) body = System.Text.RegularExpressions.Regex.Replace(body, "\"[0-9A-F]{40}\"", $"\"{hash!.ToUpperInvariant()}\"", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(2));
                return Reply(body);
            }

            return Reply("{}");
        }

        private static HttpResponseMessage Reply(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };
    }
}
