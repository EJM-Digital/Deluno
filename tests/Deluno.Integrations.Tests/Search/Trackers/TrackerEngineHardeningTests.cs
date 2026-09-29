using System.Net;
using System.Net.Http.Headers;
using Deluno.Integrations.Search.Trackers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Integrations.Tests.Search.Trackers;

/// <summary>
/// #1064: a tracker's headers, cookies and sign-in go only to the tracker's own
/// hosts; a redirect never carries a sign-in elsewhere; and a page or torrent
/// past a size cap is refused rather than read into memory.
/// </summary>
public sealed class TrackerEngineHardeningTests
{
    private const string Yaml = """
        id: hardtracker
        name: Hard Tracker
        type: private
        encoding: UTF-8
        links:
          - https://tracker.example/
          - https://mirror.example/
        caps:
          categorymappings:
            - {id: 1, cat: Movies, desc: Movies}
          modes:
            search: [q]
        settings: []
        login:
          path: login.php
          method: post
          inputs:
            username: someone
            password: hunter2
        download:
          headers:
            X-Secret: ["token-abc"]
        search:
          paths:
            - path: browse.php
          rows:
            selector: tr.row
          fields:
            title:
              selector: a.name
            download:
              selector: a.dl
              attribute: href
            size:
              selector: td.size
            seeders:
              selector: td.seeders
            leechers:
              text: 0
        """;

    private static readonly byte[] Torrent = "d8:announce30:https://tracker.example/announcee"u8.ToArray();

    private static (TrackerEngine Engine, TrackerConnection Connection) Connect(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        var definition = TrackerDefinition.Parse(Yaml);
        var engine = new TrackerEngine((_, _) => new FakeHandler(answer), TimeProvider.System, NullLogger<TrackerEngine>.Instance);
        return (engine, new TrackerConnection("indexer-1", definition, definition.Links[0], new Dictionary<string, string?>()));
    }

    private static HttpResponseMessage Ok(byte[] bytes)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private static HttpResponseMessage Redirect(HttpStatusCode status, string location)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    [Fact]
    public async Task A_download_on_the_trackers_own_host_carries_the_definitions_headers()
    {
        HttpRequestMessage? seen = null;
        var (engine, connection) = Connect(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/login.php") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
            seen = request;
            return Ok(Torrent);
        });

        var grab = await engine.ResolveDownloadAsync(connection, "https://tracker.example/download.php?id=7", CancellationToken.None);

        Assert.NotNull(grab.TorrentFile);
        Assert.True(seen!.Headers.Contains("X-Secret"));
    }

    [Fact]
    public async Task A_download_link_on_another_host_is_fetched_without_the_definitions_headers()
    {
        HttpRequestMessage? seen = null;
        var (engine, connection) = Connect(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/login.php") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
            seen = request;
            return Ok(Torrent);
        });

        var grab = await engine.ResolveDownloadAsync(connection, "https://cdn.somewhere-else.example/file.torrent", CancellationToken.None);

        Assert.NotNull(grab.TorrentFile);
        Assert.Equal("cdn.somewhere-else.example", seen!.RequestUri!.Host);
        Assert.False(seen.Headers.Contains("X-Secret"), "the definition's header must not reach a host the tracker does not own");
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080/x.torrent")]
    [InlineData("http://localhost/x.torrent")]
    [InlineData("http://192.168.1.5/x.torrent")]
    [InlineData("http://10.0.0.7/x.torrent")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://[::1]/x.torrent")]
    [InlineData("http://nas/x.torrent")]
    public async Task A_download_link_at_an_address_inside_the_network_is_never_fetched(string link)
    {
        var fetched = new List<Uri>();
        var (engine, connection) = Connect(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/login.php") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
            fetched.Add(request.RequestUri!);
            return Ok(Torrent);
        });

        await Assert.ThrowsAsync<TrackerException>(() => engine.ResolveDownloadAsync(connection, link, CancellationToken.None));
        Assert.Empty(fetched);
    }

    [Fact]
    public async Task A_redirect_from_the_tracker_to_another_host_drops_the_headers()
    {
        var hops = new List<HttpRequestMessage>();
        var (engine, connection) = Connect(request =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/login.php") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
            hops.Add(request);
            return uri.Host == "tracker.example"
                ? Redirect(HttpStatusCode.Found, "https://files.other-host.example/t.torrent")
                : Ok(Torrent);
        });

        var grab = await engine.ResolveDownloadAsync(connection, "https://tracker.example/download.php?id=7", CancellationToken.None);

        Assert.NotNull(grab.TorrentFile);
        Assert.Equal(2, hops.Count);
        Assert.True(hops[0].Headers.Contains("X-Secret"));
        Assert.False(hops[1].Headers.Contains("X-Secret"));
    }

    [Fact]
    public async Task A_redirect_between_the_trackers_own_mirrors_is_followed_with_its_headers()
    {
        var hops = new List<HttpRequestMessage>();
        var (engine, connection) = Connect(request =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/login.php") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
            hops.Add(request);
            return uri.Host == "tracker.example" ? Redirect(HttpStatusCode.MovedPermanently, "https://mirror.example/t.torrent") : Ok(Torrent);
        });

        var grab = await engine.ResolveDownloadAsync(connection, "https://tracker.example/download.php?id=7", CancellationToken.None);

        Assert.NotNull(grab.TorrentFile);
        Assert.All(hops, hop => Assert.True(hop.Headers.Contains("X-Secret")));
    }

    [Theory]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task A_sign_in_redirected_to_another_host_is_stopped_before_it_arrives(HttpStatusCode status)
    {
        var reached = new List<Uri>();
        var (engine, connection) = Connect(request =>
        {
            reached.Add(request.RequestUri!);
            return request.RequestUri!.Host == "tracker.example"
                ? Redirect(status, "https://credentials-thief.example/collect")
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
        });

        await Assert.ThrowsAsync<TrackerException>(() => engine.SearchAsync(connection, new TrackerQuery("dune"), CancellationToken.None));
        Assert.DoesNotContain(reached, uri => uri.Host == "credentials-thief.example");
    }

    [Fact]
    public async Task A_page_larger_than_the_cap_is_refused()
    {
        var (engine, connection) = Connect(request => request.RequestUri!.AbsolutePath == "/login.php"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") }
            : Ok(new byte[TrackerEngine.MaxPageBytes + 1]));

        var exception = await Assert.ThrowsAsync<TrackerException>(() => engine.SearchAsync(connection, new TrackerQuery("dune"), CancellationToken.None));
        Assert.Contains("will not read", exception.Message);
    }

    [Fact]
    public async Task A_torrent_larger_than_its_cap_is_refused()
    {
        var oversized = new byte[TrackerEngine.MaxTorrentBytes + 1];
        oversized[0] = (byte)'d';
        var (engine, connection) = Connect(request => request.RequestUri!.AbsolutePath == "/login.php"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") }
            : Ok(oversized));

        await Assert.ThrowsAsync<TrackerException>(() => engine.ResolveDownloadAsync(connection, "https://tracker.example/download.php?id=7", CancellationToken.None));
    }

    [Fact]
    public async Task A_stream_with_no_declared_length_is_cut_off_at_the_cap()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(1024 * 1024, 20) };

        await Assert.ThrowsAsync<TrackerException>(() => TrackerEngine.ReadCappedAsync(response, 5 * 1024 * 1024, CancellationToken.None));
    }

    [Fact]
    public async Task A_body_within_the_cap_is_read_whole()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(1024, 4) };

        var bytes = await TrackerEngine.ReadCappedAsync(response, 5 * 1024 * 1024, CancellationToken.None);

        Assert.Equal(4096, bytes.Length);
    }

    [Theory]
    [InlineData("http://172.16.0.1/", true)]
    [InlineData("http://172.32.0.1/", false)]
    [InlineData("http://100.64.0.1/", true)]
    [InlineData("http://[fd00::1]/", true)]
    [InlineData("https://tracker.example/", false)]
    [InlineData("https://8.8.8.8/", false)]
    public void The_host_guard_names_addresses_inside_a_network(string address, bool internalAddress)
        => Assert.Equal(internalAddress, TrackerHostGuard.IsInternal(new Uri(address)));

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = answer(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    /// <summary>Content that streams chunks and never says how long it is, the way a chunked reply does.</summary>
    private sealed class UnknownLengthContent(int chunkSize, int chunks) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            for (var i = 0; i < chunks; i++)
            {
                await stream.WriteAsync(new byte[chunkSize]);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
