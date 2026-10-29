using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// "Connect the apps you installed" when the request is wrong (coverage register, area guided-setup, low rows): an app Deluno does not
/// know is a 404 and a body that is not JSON is a 400 in plain words, and neither saves a connection.
/// </summary>
public sealed class InstalledAppsConnectRefusalsLowRowsTests
{
    [Fact]
    public async Task An_unknown_app_kind_is_a_404_and_a_body_that_is_not_JSON_is_a_400_in_plain_words_and_saves_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var unknown = await app.Client.PostAsync("/api/system/bundled-components/carrier-pigeon/connect", content: null);
        var broken = await app.Client.PostAsync("/api/system/bundled-components/qbittorrent/connect", new StringContent("{ not json", Encoding.UTF8, new MediaTypeHeaderValue("application/json")));

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);
        Assert.Equal("The request body is not valid JSON.", JsonDocument.Parse(await broken.Content.ReadAsStringAsync()).RootElement.GetProperty("message").GetString());
        Assert.Equal("[]", (await app.Client.GetStringAsync("/api/download-clients")).Replace(" ", ""));
    }
}
