using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1079: a caller that saves a library without mentioning its downloads folder (a script that only
/// renames it) must not wipe the folder. Sending the field, empty or null, still clears it.
/// </summary>
public sealed class ALibraryEditLeavesAnOmittedDownloadsFolderAloneTests
{
    [Fact]
    public async Task A_rename_that_leaves_the_downloads_folder_out_keeps_it_and_sending_it_empty_clears_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "films")).FullName;
        var downloads = Directory.CreateDirectory(Path.Combine(app.DataRoot, "incoming")).FullName;
        var response = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name = "Films",
            mediaType = "movies",
            purpose = "collection",
            rootPath = root,
            downloadsPath = downloads,
            qualityProfileId = (string?)null,
            importWorkflow = "copy",
            processorName = (string?)null,
            processorOutputPath = (string?)null,
            processorTimeoutMinutes = (int?)null,
            processorFailureMode = (string?)null,
            autoSearchEnabled = false,
            missingSearchEnabled = false,
            upgradeSearchEnabled = false,
            searchIntervalMinutes = (int?)null,
            retryDelayHours = (int?)null,
            maxItemsPerRun = (int?)null
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var id = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;

        var renamed = await Put(app, id, new { name = "Movies", rootPath = root });
        Assert.Equal("Movies", renamed.GetProperty("name").GetString());
        Assert.Equal(downloads, renamed.GetProperty("downloadsPath").GetString());

        var cleared = await Put(app, id, new { name = "Movies", rootPath = root, downloadsPath = "" });
        Assert.True(cleared.GetProperty("downloadsPath").ValueKind == JsonValueKind.Null
                    || string.IsNullOrEmpty(cleared.GetProperty("downloadsPath").GetString()));

        var set = await Put(app, id, new { name = "Movies", rootPath = root, downloadsPath = downloads });
        Assert.Equal(downloads, set.GetProperty("downloadsPath").GetString());
        var clearedWithNull = await Put(app, id, new { name = "Movies", rootPath = root, downloadsPath = (string?)null });
        Assert.Equal(JsonValueKind.Null, clearedWithNull.GetProperty("downloadsPath").ValueKind);
    }

    private static async Task<JsonElement> Put(ApplicationTestHost app, string id, object body)
    {
        var response = await app.Client.PutAsync(
            $"/api/libraries/{id}",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }
}
