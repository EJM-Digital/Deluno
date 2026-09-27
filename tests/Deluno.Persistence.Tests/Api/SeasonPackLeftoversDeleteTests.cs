using Deluno.Persistence.Tests.Support;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #889 (cleanup sheet E9): a season pack's leftovers - an extra, sample, or
/// unlisted special that never resolved to a canonical episode - are never
/// deleted on their own. This is the screen action that lets a person
/// confirm the deletion once they have seen what did not match.
/// </summary>
public sealed class SeasonPackLeftoversDeleteTests
{
    [Fact]
    public async Task Confirming_deletes_a_leftover_under_the_library_s_downloads_path()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var downloadsPath = Path.Combine(app.DataRoot, "downloads");
        Directory.CreateDirectory(downloadsPath);
        await CreateLibraryAsync(app, downloadsPath);

        var packDirectory = Path.Combine(downloadsPath, "Some.Show.S01");
        Directory.CreateDirectory(packDirectory);
        var extraPath = Path.Combine(packDirectory, "Some.Show.S01.Extras.Making.Of.mkv");
        await File.WriteAllTextAsync(extraPath, "not an episode");

        var response = await app.Client.PostAsJsonAsync(
            "/api/filesystem/import/season-pack/leftovers/delete",
            new { paths = new[] { extraPath } });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, body.RootElement.GetProperty("deleted").GetInt32());
        Assert.False(File.Exists(extraPath));

        var feed = await app.Client.GetAsync("/api/activity?pageSize=50");
        using var feedBody = JsonDocument.Parse(await feed.Content.ReadAsStringAsync());
        Assert.Contains(
            feedBody.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("category").GetString() == "filesystem.import.season-pack.leftovers-deleted");
    }

    [Fact]
    public async Task A_path_outside_every_configured_downloads_folder_is_refused_and_kept()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var downloadsPath = Path.Combine(app.DataRoot, "downloads");
        Directory.CreateDirectory(downloadsPath);
        await CreateLibraryAsync(app, downloadsPath);

        var outsideDirectory = Path.Combine(app.DataRoot, "elsewhere");
        Directory.CreateDirectory(outsideDirectory);
        var outsidePath = Path.Combine(outsideDirectory, "not-a-leftover.mkv");
        await File.WriteAllTextAsync(outsidePath, "should never be touched");

        var response = await app.Client.PostAsJsonAsync(
            "/api/filesystem/import/season-pack/leftovers/delete",
            new { paths = new[] { outsidePath } });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, body.RootElement.GetProperty("deleted").GetInt32());
        Assert.Contains(
            body.RootElement.GetProperty("refused").EnumerateArray(),
            item => item.GetString() == outsidePath);
        Assert.True(File.Exists(outsidePath));
    }

    [Fact]
    public async Task An_empty_request_is_refused()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync(
            "/api/filesystem/import/season-pack/leftovers/delete",
            new { paths = Array.Empty<string>() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task CreateLibraryAsync(ApplicationTestHost app, string downloadsPath)
    {
        var rootPath = Path.Combine(app.DataRoot, "series");
        Directory.CreateDirectory(rootPath);
        var response = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name = "Shows",
            mediaType = "tv",
            purpose = "collection",
            rootPath,
            downloadsPath,
            qualityProfileId = (string?)null,
            importWorkflow = "copy",
            processorName = (string?)null,
            processorOutputPath = (string?)null,
            processorTimeoutMinutes = (int?)null,
            processorFailureMode = (string?)null,
            autoSearchEnabled = false,
            missingSearchEnabled = false,
            upgradeSearchEnabled = false,
            searchIntervalHours = (int?)null,
            retryDelayHours = (int?)null,
            maxItemsPerRun = (int?)null
        });

        Assert.True(
            response.IsSuccessStatusCode,
            $"POST /api/libraries/ returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }
}
