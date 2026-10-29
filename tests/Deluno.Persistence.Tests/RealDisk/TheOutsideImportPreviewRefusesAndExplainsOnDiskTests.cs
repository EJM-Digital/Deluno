using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// The outside-app import preview on a real host: a body that is not a preview request is refused 400, a key that may only
/// read is refused 403, no key at all is refused 401, and a source file that is not on the disk is reported inside the
/// preview (200, source does not exist) instead of as an error status. A real file still reads as existing.
/// </summary>
public sealed class TheOutsideImportPreviewRefusesAndExplainsOnDiskTests
{
    private const string Route = "/api/integrations/external/import-preview";

    private static async Task<HttpClient> KeyAsync(ApplicationTestHost app, string scopes)
    {
        var created = await app.Client.PostAsJsonAsync("/api/api-keys/", new { name = "Preview " + scopes, scopes });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var client = app.CreateAnonymousClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!);
        return client;
    }

    private static object Body(string source) => new
    {
        sourcePath = source,
        mediaType = "movies",
        title = "Arrival",
        year = 2016,
        genres = Array.Empty<string>(),
        tags = Array.Empty<string>()
    };

    [Fact]
    public async Task A_body_that_is_not_a_preview_request_is_refused_400()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        using var notJson = await app.Client.PostAsync(Route, new StringContent("this is not json", Encoding.UTF8, "application/json"));
        using var empty = await app.Client.PostAsync(Route, new StringContent(string.Empty, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, notJson.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [Fact]
    public async Task A_key_that_may_only_read_is_refused_403_no_key_is_refused_401_and_the_imports_key_is_answered()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var disk = await RealDiskLibrary.OpenAsync(app);
        var source = disk.WriteDownload("Arrival.2016.1080p.WEB-DL.x264-GRP.mkv");
        var readOnly = await KeyAsync(app, "read");
        var imports = await KeyAsync(app, "imports");

        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PostAsJsonAsync(Route, Body(source))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateAnonymousClient().PostAsJsonAsync(Route, Body(source))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await imports.PostAsJsonAsync(Route, Body(source))).StatusCode);
    }

    [Fact]
    public async Task A_source_file_that_is_not_on_the_disk_is_reported_in_the_preview_and_a_real_one_is_not()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var disk = await RealDiskLibrary.OpenAsync(app);
        var present = disk.WriteDownload("Arrival.2016.1080p.WEB-DL.x264-GRP.mkv");
        var missing = Path.Combine(disk.Downloads, "Gone.2016.1080p.WEB-DL.x264-GRP.mkv");

        using var absentResponse = await app.Client.PostAsJsonAsync(Route, Body(missing));
        using var presentResponse = await app.Client.PostAsJsonAsync(Route, Body(present));

        Assert.Equal(HttpStatusCode.OK, absentResponse.StatusCode);
        var absent = await absentResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(absent.GetProperty("sourceExists").GetBoolean());
        Assert.Equal(0, absent.GetProperty("sourceSizeBytes").GetInt64());
        Assert.False(File.Exists(missing));

        var there = await presentResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(there.GetProperty("sourceExists").GetBoolean());
        Assert.Equal(4096, there.GetProperty("sourceSizeBytes").GetInt64());
    }
}
