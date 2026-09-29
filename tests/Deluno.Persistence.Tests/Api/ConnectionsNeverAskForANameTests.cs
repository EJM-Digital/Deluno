using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1076: an API client that sends no name, or a made-up one, is not refused and
/// does not name the connection: the server names it from what it is.
/// </summary>
public sealed class ConnectionsNeverAskForANameTests
{
    private static readonly string Machine = DelunoHostName.Current;

    [Theory]
    [InlineData("/api/download-clients", """{"protocol":"qbittorrent","host":"localhost","port":8080,"isEnabled":true}""", "qBittorrent on {machine}")]
    [InlineData("/api/download-clients", """{"name":"Mine","protocol":"sabnzbd","host":"192.168.1.51","port":8085,"isEnabled":true}""", "SABnzbd on 192.168.1.51")]
    [InlineData("/api/indexers", """{"protocol":"newznab","baseUrl":"https://api.nzbgeek.info","apiKey":"k","mediaScope":"both","isEnabled":true}""", "nzbgeek.info")]
    [InlineData("/api/notification-webhooks", """{"url":"https://discord.com/api/webhooks/1/x","eventFilters":"all","provider":"discord","isEnabled":true}""", "Discord on discord.com")]
    [InlineData("/api/media-servers", """{"kind":"jellyfin","baseUrl":"http://127.0.0.1:8096","token":"t","isEnabled":true}""", "Jellyfin on {machine}")]
    [InlineData("/api/media-servers", """{"kind":"plex","name":"Mine","baseUrl":"http://nas.lan:32400","token":"t","isEnabled":true}""", "Plex on nas.lan")]
    [InlineData("/api/seerr-servers", """{"baseUrl":"http://request.lan:5055","apiKey":"k","isEnabled":true}""", "Seerr on request.lan")]
    [InlineData("/api/integrations/processors/connections", """{"submissionUrl":"http://192.168.1.60:1/hook","isEnabled":true}""", "Processor on 192.168.1.60")]
    public async Task A_connection_saved_without_a_name_is_named_from_its_address(string path, string body, string expected)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsync(path, new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var saved = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expected.Replace("{machine}", Machine), saved.GetProperty("name").GetString());
    }
}
