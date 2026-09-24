using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// The Cloudflare helper's address is a setting that stays put (#628): saved
/// with a patch, kept by a patch about something else, cleared by an empty one.
/// </summary>
public sealed class CloudflareHelperSettingTests
{
    [Fact]
    public async Task The_helper_address_is_saved_kept_and_cleared()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        await Patch(app, new { cloudflareHelperUrl = "http://127.0.0.1:8191/" });
        Assert.Equal("http://127.0.0.1:8191", await HelperUrl(app));

        await Patch(app, new { appInstanceName = "Rig" });
        Assert.Equal("http://127.0.0.1:8191", await HelperUrl(app));

        await Patch(app, new { cloudflareHelperUrl = "" });
        Assert.Null(await HelperUrl(app));
    }

    [Fact]
    public async Task Checking_an_address_where_nothing_answers_says_so()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/trackers/cloudflare-helper/test", new { url = "http://127.0.0.1:1" });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(body.GetProperty("healthy").GetBoolean());
        Assert.Contains("Nothing answered", body.GetProperty("message").GetString());
    }

    private static async Task Patch(ApplicationTestHost app, object patch)
    {
        var response = await app.Client.PatchAsJsonAsync("/api/settings", patch);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<string?> HelperUrl(ApplicationTestHost app)
    {
        var settings = await app.Client.GetFromJsonAsync<JsonElement>("/api/settings");
        return settings.TryGetProperty("cloudflareHelperUrl", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
