using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1061: every qBittorrent call signs in the way the connection test does, so a login that passed
/// the test is not refused by the save-path and free-space checks, and a hiccup right after an
/// install is tried again before it is reported.
/// </summary>
public sealed class QbittorrentSignInTests
{
    private const string Categories = """{"deluno-movies":{"name":"deluno-movies","savePath":"C:/Downloads/deluno-movies"}}""";

    [Fact]
    public async Task A_sign_in_that_answers_without_the_words_Ok_but_gives_a_working_session_is_accepted()
    {
        // qBittorrent 5.2.1 on the clean VM: the connection test passed (it asks for the version after
        // the login) while the category check refused the same login because the body was not "Ok.".
        var handler = new FakeQbittorrent(loginBodies: ["{}"]);

        var check = await new QbittorrentDownloadClient(() => handler).CheckCategoryAsync(Client(), "deluno-movies", CancellationToken.None);

        Assert.Equal(DownloadClientCategoryStatuses.Ready, check.Status);
        Assert.Equal("C:/Downloads/deluno-movies", check.SavePath);
        Assert.Equal(1, handler.Logins);
    }

    [Fact]
    public async Task A_rejection_right_after_install_is_tried_once_more_and_then_succeeds()
    {
        var handler = new FakeQbittorrent(loginBodies: ["Fails.", "Ok."]);

        var check = await new QbittorrentDownloadClient(() => handler).CheckCategoryAsync(Client(), "deluno-movies", CancellationToken.None);

        Assert.Equal(DownloadClientCategoryStatuses.Ready, check.Status);
        Assert.Equal(2, handler.Logins);
    }

    [Fact]
    public async Task A_login_that_is_really_wrong_is_reported_as_the_client_not_answering_not_as_an_error()
    {
        var handler = new FakeQbittorrent(loginBodies: ["Fails."], sessionWorks: false);

        var check = await new QbittorrentDownloadClient(() => handler).CheckCategoryAsync(Client(), "deluno-movies", CancellationToken.None);

        Assert.Equal(DownloadClientCategoryStatuses.Unreachable, check.Status);
        Assert.Contains("refused Deluno's username and password", check.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.Logins);
    }

    [Fact]
    public async Task A_banned_address_is_not_retried()
    {
        var handler = new FakeQbittorrent(loginBodies: ["Ok."], loginStatus: HttpStatusCode.Forbidden);

        var check = await new QbittorrentDownloadClient(() => handler).CheckCategoryAsync(Client(), "deluno-movies", CancellationToken.None);

        Assert.Equal(DownloadClientCategoryStatuses.Unreachable, check.Status);
        Assert.Equal(1, handler.Logins);
    }

    private static DownloadClientItem Client()
        => new(
            Id: "qbit-1", Name: "qBittorrent", Protocol: "qbittorrent", Host: "localhost", Port: 8081,
            Username: "deluno", Secret: "generated", EndpointUrl: "http://localhost:8081/", UseSsl: false, UrlBase: null,
            MoviesCategory: "deluno-movies", TvCategory: "deluno-tv", CategoryTemplate: null, Priority: 1, IsEnabled: true,
            HealthStatus: "healthy", LastHealthMessage: null, LastHealthFailureCategory: null, LastHealthLatencyMs: null,
            LastHealthTestUtc: null, CreatedUtc: DateTimeOffset.UtcNow, UpdatedUtc: DateTimeOffset.UtcNow);

    private sealed class FakeQbittorrent(string[] loginBodies, bool sessionWorks = true, HttpStatusCode loginStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Logins { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.Contains("auth/login", StringComparison.Ordinal))
            {
                var body = loginBodies[Math.Min(Logins, loginBodies.Length - 1)];
                Logins++;
                return Task.FromResult(new HttpResponseMessage(loginStatus) { Content = new StringContent(body, Encoding.UTF8, "text/plain") });
            }

            if (!sessionWorks)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            }

            var json = path.Contains("torrents/categories", StringComparison.Ordinal) ? Categories : "5.2.1";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
