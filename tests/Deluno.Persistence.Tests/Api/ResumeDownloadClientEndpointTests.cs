using Deluno.Persistence.Tests.Support;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// <c>POST /api/download-clients/{clientId}/resume</c> resumes a client paused as
/// a whole. It is signed in like its neighbours, and says in plain words when it
/// cannot: no such client, a client that has no pause of its own, or one that
/// cannot be reached. What SABnzbd and NZBGet do with the call is covered where
/// their adapters are tested.
/// </summary>
public sealed class ResumeDownloadClientEndpointTests
{
    [Fact]
    public async Task It_needs_a_signed_in_user()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var clientId = await CreateClientAsync(app, "sabnzbd");

        var response = await app.CreateAnonymousClient().PostAsync($"/api/download-clients/{clientId}/resume", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_client_that_is_gone_says_so()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var (status, message) = await ResumeAsync(app, "no-such-client");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("Download client was not found.", message);
    }

    [Fact]
    public async Task A_torrent_client_has_no_whole_client_pause_to_resume()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var clientId = await CreateClientAsync(app, "qbittorrent");

        var (status, message) = await ResumeAsync(app, clientId);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("nothing to resume", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_usenet_client_that_cannot_be_reached_is_reported_not_resumed()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var clientId = await CreateClientAsync(app, "sabnzbd", port: 1);

        var (status, message) = await ResumeAsync(app, clientId);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    private static async Task<(HttpStatusCode Status, string Message)> ResumeAsync(ApplicationTestHost app, string clientId)
    {
        var response = await app.Client.PostAsync($"/api/download-clients/{clientId}/resume", content: null);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, document.RootElement.GetProperty("message").GetString() ?? string.Empty);
    }

    private static async Task<string> CreateClientAsync(ApplicationTestHost app, string protocol, int port = 8080)
    {
        var response = await app.Client.PostAsJsonAsync("/api/download-clients/", new
        {
            name = protocol,
            protocol,
            host = "127.0.0.1",
            port,
            username = (string?)null,
            password = "an-api-key",
            endpointUrl = (string?)null,
            moviesCategory = "movies",
            tvCategory = "tv",
            categoryTemplate = (string?)null,
            priority = 1,
            isEnabled = true
        });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }
}
