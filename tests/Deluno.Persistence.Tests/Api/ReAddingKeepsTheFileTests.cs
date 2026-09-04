using Deluno.Contracts;
using Deluno.Media;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using System.Text.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Adding a title you already hold does not make Deluno forget its file.
///
/// <para><b>The lab defect, explained.</b> Big Buck Bunny read
/// <c>hasFile=false</c> while still carrying a <c>filePath</c> and a
/// <c>fileSizeBytes</c> of 61,878,609 — a row that knew the size of a file it
/// claimed not to have — and reconciliation called that same, intact file an
/// orphan. Two symptoms, one cause.</para>
///
/// <para><c>POST /api/movies</c> dedupes: a title the catalogue already holds
/// comes back as the row it already has. The endpoint then went on to write
/// wanted state with <c>hasFile: false</c> hardcoded, and
/// <c>EnsureWantedStateAsync</c> upserts with
/// <c>has_file = excluded.has_file</c>. So the second add cleared the flag on a
/// film that had a file, and left the path on the entry alone — because the
/// path lives on <c>movie_entries</c> and the flag lives on
/// <c>movie_wanted_state</c>.</para>
///
/// <para>The orphan report follows from the same clearing: reconciliation
/// selects tracked files with <c>has_file = 1</c>, so a file whose flag has
/// been cleared is a file nothing owns.</para>
///
/// <para>And it is worse than a wrong badge. The title returns to the wanted
/// list, so Deluno goes and downloads a film it is already holding — which is
/// the one thing a library manager must never do.</para>
/// </summary>
public sealed class ReAddingKeepsTheFileTests
{
    [Theory]
    [InlineData(MediaKind.Movie)]
    [InlineData(MediaKind.Series)]
    public async Task Adding_a_title_twice_does_not_clear_the_file_it_already_had(MediaKind kind)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await CreateLibraryAsync(app, kind);
        var route = kind == MediaKind.Movie ? "/api/movies/" : "/api/series/";
        var body = kind == MediaKind.Movie
            ? (object)new { title = "Big Buck Bunny", releaseYear = 2008, monitored = true }
            : new { title = "Big Buck Bunny", startYear = 2008, monitored = true };

        var id = await AddAsync(app, route, body);

        // The title now has a file, the way an import leaves it.
        var state = app.Services.GetRequiredService<IMediaStateRepository>();
        await state.EnsureWantedStateAsync(
            kind,
            id,
            libraryId,
            WantedStatuses.Covered,
            "Imported from disk.",
            hasFile: true,
            currentQuality: "Bluray-1080p",
            targetQuality: "Bluray-1080p",
            qualityCutoffMet: true,
            CancellationToken.None);

        Assert.True(await HasFileAsync(app, route, id), "the title should hold a file before it is re-added");

        // The same title again. The catalogue collapses it onto the row it has,
        // which is correct — and must not cost the file.
        var second = await AddAsync(app, route, body);
        Assert.Equal(id, second);

        Assert.True(
            await HasFileAsync(app, route, id),
            "re-adding a title Deluno already holds cleared the record that its file exists");
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<bool> HasFileAsync(ApplicationTestHost app, string route, string id)
    {
        var response = await app.Client.GetAsync($"{route.TrimEnd('/')}/{id}");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("hasFile").GetBoolean();
    }

    /// <summary>
    /// A real library of the right kind, created through the API.
    ///
    /// <para>Not optional, and the first version of this test was worthless
    /// without it. The endpoint writes wanted state inside
    /// <c>foreach (var library in libraries)</c>, and a fresh install has none —
    /// so with no library the loop never runs, nothing is overwritten, and the
    /// test passed just as happily against the unfixed code. The defect only
    /// exists where a library does.</para>
    /// </summary>
    private static async Task<string> CreateLibraryAsync(ApplicationTestHost app, MediaKind kind)
    {
        var mediaType = kind == MediaKind.Movie ? "movies" : "tv";
        var rootPath = Path.Combine(app.DataRoot, mediaType);
        Directory.CreateDirectory(rootPath);

        var response = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name = mediaType,
            mediaType,
            purpose = "collection",
            rootPath,
            downloadsPath = (string?)null,
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

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<string> AddAsync(ApplicationTestHost app, string route, object body)
    {
        var response = await app.Client.PostAsJsonAsync(route, body);
        Assert.True(
            response.IsSuccessStatusCode,
            $"POST {route} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }
}
