using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1278: the drawer's Test button for uTorrent, through the real route and the real HTTP client, against uTorrent 3.6's own replies
/// (tests/fixtures/recorded/utorrent) served over a loopback port: the token page it gives, the 401 it gives a wrong login, and another
/// app's page at the same address.
/// </summary>
public sealed class UTorrentConnectionTestOnRecordedAppTests
{
    private static object Form(RecordedAppServer server) => new
    {
        name = "uTorrent",
        protocol = "utorrent",
        host = server.Host,
        port = server.Port,
        username = "deluno",
        password = "a-test-password",
        moviesCategory = "movies",
        tvCategory = "tv",
        priority = 10,
        isEnabled = true
    };

    private static async Task<JsonElement> TestAsync(RecordedAppServer server)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var response = await app.Client.PostAsJsonAsync("/api/download-clients/test", Form(server));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task The_token_page_uTorrent_gives_is_a_healthy_connection_and_the_only_call_made_is_that_page_with_the_login()
    {
        await using var uTorrent = await RecordedAppServer.StartAsync("utorrent", [RecordedReplies.Utorrent("token")]);

        var body = await TestAsync(uTorrent);

        Assert.Equal("healthy", body.GetProperty("healthStatus").GetString());
        Assert.Contains("Connected to uTorrent", body.GetProperty("message").GetString()!, StringComparison.Ordinal);
        Assert.Equal("/gui/token.html", Assert.Single(uTorrent.Requests).PathAndQuery);
        Assert.Empty(uTorrent.Unanswered);
    }

    [Fact]
    public async Task A_wrong_login_is_the_refused_login_wording_with_the_auth_category()
    {
        await using var uTorrent = await RecordedAppServer.StartAsync("utorrent", [RecordedReplies.Utorrent("login-refused")]);

        var body = await TestAsync(uTorrent);

        Assert.Equal("degraded", body.GetProperty("healthStatus").GetString());
        Assert.Equal("auth", body.GetProperty("failureCategory").GetString());
        Assert.Contains("refused Deluno's username and password", body.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_address_that_answers_the_token_page_with_another_apps_page_is_unexpected_response_not_connected()
    {
        var wrongApp = RecordedReplies.Qbittorrent("version").Exchanges[0].Reply;
        await using var uTorrent = await RecordedAppServer.StartAsync("utorrent", [RecordedReplies.Utorrent("token")], (_, _) => wrongApp.ToResponse());

        var body = await TestAsync(uTorrent);

        Assert.Equal("degraded", body.GetProperty("healthStatus").GetString());
        Assert.Equal("unexpected-response", body.GetProperty("failureCategory").GetString());
    }
}
