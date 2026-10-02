using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Integrations.MediaServers;

namespace Deluno.Integrations.Tests.MediaServers;

/// <summary>#1117: a media server's test carries the version it reported, from a recorded answer; no version means nothing is shown.</summary>
public sealed class MediaServerVersionTests
{
    private static MediaServerConnection Server(string kind, string baseUrl)
        => new("server-1", kind, "Rig " + kind, baseUrl, "token-123", true, "untested", null, null, null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Plex_reports_the_version_from_its_root_answer()
    {
        var client = new PlexMediaServerClient(Factory(request => request.RequestUri!.AbsolutePath switch
        {
            "/" => Json("""{"MediaContainer":{"machineIdentifier":"abc123","friendlyName":"Plex Rig","version":"1.43.4.10903-e5521bd8c"}}"""),
            "/library/sections" => Json("""{"MediaContainer":{"Directory":[{"key":"1","type":"movie"}]}}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        }));

        var result = await client.TestAsync(Server(MediaServerKinds.Plex, "http://192.168.1.51:32400"), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("1.43.4.10903-e5521bd8c", result.Version);
    }

    [Fact]
    public async Task Plex_that_sends_no_version_has_none()
    {
        var client = new PlexMediaServerClient(Factory(request => request.RequestUri!.AbsolutePath switch
        {
            "/" => Json("""{"MediaContainer":{"machineIdentifier":"abc123","friendlyName":"Plex Rig"}}"""),
            _ => Json("""{"MediaContainer":{"Directory":[]}}""")
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
            "/System/Info" => Json("""{"ServerName":"jf","Id":"abc","Version":"10.10.7"}"""),
            "/Library/VirtualFolders" => Json("""[{"Name":"Movies"}]"""),
            "/Users" => Json("""[{"Id":"u1","Name":"James"}]"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        }));

        var result = await client.TestAsync(Server(MediaServerKinds.Jellyfin, "http://192.168.1.51:8096"), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("10.10.7", result.Version);
    }

    [Fact]
    public async Task Emby_reports_its_version_even_when_its_key_cannot_list_users()
    {
        var client = new EmbyMediaServerClient(Factory(request => request.RequestUri!.AbsolutePath switch
        {
            "/emby/System/Info" => Json("""{"ServerName":"emby","Id":"abc","Version":"4.9.1.80"}"""),
            "/emby/Library/VirtualFolders" => Json("""[]"""),
            _ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        }));

        var result = await client.TestAsync(Server(MediaServerKinds.Emby, "http://192.168.1.51:8097"), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("4.9.1.80", result.Version);
    }

    [Fact]
    public async Task A_server_that_does_not_answer_has_no_version_and_no_error()
    {
        var client = new JellyfinMediaServerClient(Factory(_ => throw new HttpRequestException("refused")));

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
