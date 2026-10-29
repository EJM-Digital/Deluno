using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1329: the System timeline's row-count setting over HTTP: read back as saved, taken at both ends of the allowed range, and
/// refused outside it with the reason in words and the saved value kept.
/// </summary>
public sealed class ActivityDisplaySettingsRoutesRowsTests
{
    private static async Task<int> PageSizeAsync(ApplicationTestHost app)
        => (await app.Client.GetFromJsonAsync<JsonElement>("/api/activity/display-settings")).GetProperty("pageSize").GetInt32();

    [Fact]
    public async Task The_page_size_is_read_back_as_it_was_saved()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var first = await PageSizeAsync(app);
        Assert.InRange(first, 25, 2000);

        var saved = await app.Client.PutAsJsonAsync("/api/activity/display-settings", new { pageSize = 500 });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(500, await PageSizeAsync(app));
    }

    [Theory]
    [InlineData(25)]
    [InlineData(2000)]
    public async Task The_edges_of_the_allowed_range_are_taken(int size)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var saved = await app.Client.PutAsJsonAsync("/api/activity/display-settings", new { pageSize = size });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(size, await PageSizeAsync(app));
    }

    [Theory]
    [InlineData(24)]
    [InlineData(2001)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_size_outside_25_to_2000_is_a_400_in_words_and_the_saved_size_is_kept(int size)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await app.Client.PutAsJsonAsync("/api/activity/display-settings", new { pageSize = 300 });

        var refused = await app.Client.PutAsJsonAsync("/api/activity/display-settings", new { pageSize = size });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        using var document = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        Assert.Equal("Page size must be between 25 and 2000.", document.RootElement.GetProperty("errors").GetProperty("pageSize")[0].GetString());
        Assert.Equal(300, await PageSizeAsync(app));
    }

    [Fact]
    public async Task Both_routes_need_a_signed_in_caller()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var anonymous = app.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/activity/display-settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync("/api/activity/display-settings", new { pageSize = 100 })).StatusCode);
    }
}
