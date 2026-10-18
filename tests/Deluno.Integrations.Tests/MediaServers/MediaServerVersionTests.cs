using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Integrations.MediaServers;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.MediaServers;

/// <summary>
/// #1117: a media server's test carries the version it reported, from the recorded answer of the real server (Plex 1.43.4, Jellyfin 12.2.0 and
/// Emby 4.10.1.0, tests/fixtures/recorded); no version means nothing is shown.
/// </summary>
public sealed class MediaServerVersionTests
{
    private static MediaServerConnection Server(string kind, string baseUrl)
        => new("server-1", kind, "Rig " + kind, baseUrl, "token-123", true, "untested", null, null, null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Plex_reports_the_version_from_its_root_answer()
    {
        var client = new PlexMediaServerClient(Factory(request => request.RequestUri!.AbsolutePath switch
        {
            "/" => RecordedReplies.Plex("root").Single.Reply.ToResponse(),
            "/library/sections" => RecordedReplies.Plex("sections").Single.Reply.ToResponse(),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        }));

        var result = await client.TestAsync(Server(MediaServerKinds.Plex, "http://192.168.1.51:32400"), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("1.43.4.10903-e5521bd8c", result.Version);
    }

    [Fact]
    public async Task Plex_that_sends_no_version_has_none()
    {
        // The recorded root with only its version taken out, and the recorded empty library list.
        var client = new PlexMediaServerClient(Factory(request => request.RequestUri!.AbsolutePath switch
        {
            "/" => Json(RecordedReplies.Plex("root").Single.Reply.BodyWith(("MediaContainer.version", null))),
            _ => RecordedReplies.Plex("libraries-created").Exchanges[0].Reply.ToResponse()
        }));

        var result = await client.TestAsync(Server(MediaServerKinds.Plex, "http://192.168.1.51:32400"), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Null(result.Version);
    }

    [Fact]
    public async Task Jellyfin_reports_the_version_from_system_info()
    {
        var client = new JellyfinMediaServerClient(Factory(request => request.RequestUri!.AbsolutePath switch
        {
            "/System/Info" => RecordedReplies.Jellyfin("identity").Exchanges[1].Reply.ToResponse(),
            "/Library/VirtualFolders" => RecordedReplies.Jellyfin("libraries").Exchanges[0].Reply.ToResponse(),
            "/Users" => RecordedReplies.Jellyfin("libraries").Exchanges[1].Reply.ToResponse(),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        }));

        var result = await client.TestAsync(Server(MediaServerKinds.Jellyfin, "http://192.168.1.51:8096"), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("12.2.0", result.Version);
    }

    [Fact]
    public async Task Emby_reports_its_version_even_when_its_key_cannot_list_users()
    {
        var client = new EmbyMediaServerClient(Factory(request => request.RequestUri!.AbsolutePath switch
        {
            "/emby/System/Info" => RecordedReplies.Emby("identity").Exchanges[1].Reply.ToResponse(),
            "/emby/Library/VirtualFolders" => RecordedReplies.Emby("libraries").Exchanges[0].Reply.ToResponse(),
            // Emby 4.10.1.0's own 401 for the user list asked with a key it does not accept.
            _ => RecordedReplies.Emby("wrong-token").Exchanges[1].Reply.ToResponse()
        }));

        var result = await client.TestAsync(Server(MediaServerKinds.Emby, "http://192.168.1.51:8097"), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("4.10.1.0", result.Version);
    }

    [Fact]
    public async Task A_server_that_does_not_answer_has_no_version_and_no_error()
    {
        var client = new JellyfinMediaServerClient(new StubFactory(new HttpClient(RecordedReplies.Jellyfin("not-answering").NotAnswering())));

        var result = await client.TestAsync(Server(MediaServerKinds.Jellyfin, "http://192.168.1.51:8096"), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Null(result.Version);
    }

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static IHttpClientFactory Factory(Func<HttpRequestMessage, HttpResponseMessage> send)
        => new StubFactory(new HttpClient(new StubHandler(send)));

    private sealed class StubFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
