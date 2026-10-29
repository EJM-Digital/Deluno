using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1329: what the existing-media import shows before and after a run, over HTTP on a real library folder: the preview of what
/// is in the folder, and the titles the run set aside.
/// </summary>
public sealed class TheExistingMediaImportReadsLowRowsTests
{
    private static async Task<string> LibraryWithFilmsAsync(ApplicationTestHost app, params string[] films)
    {
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var root = await ApiScenes.LibraryRootAsync(app, libraryId);
        foreach (var film in films)
        {
            var file = Path.Combine(root, film, film + " Bluray-1080p.mkv");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllBytesAsync(file, [1, 2, 3]);
        }

        return libraryId;
    }

    [Fact]
    public async Task The_preview_lists_the_films_in_the_library_folder_without_importing_any_and_an_unknown_library_is_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await LibraryWithFilmsAsync(app, "Dune (2021)", "Heat (1995)");

        var response = await app.Client.GetAsync($"/api/libraries/{libraryId}/import-existing/preview");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("Dune", text, StringComparison.Ordinal);
        Assert.Contains("Heat", text, StringComparison.Ordinal);
        Assert.Equal(0, await ApiScenes.ScalarAsync(app, "movies", "SELECT COUNT(*) FROM movie_entries;"));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/api/libraries/no-such-library/import-existing/preview")).StatusCode);
    }

    [Fact]
    public async Task The_issues_list_the_titles_the_run_set_aside_with_why_and_a_library_with_no_run_lists_none()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await LibraryWithFilmsAsync(app, "Dune (2021)");
        Assert.Empty((await app.Client.GetFromJsonAsync<JsonElement>($"/api/libraries/{libraryId}/import-existing/issues")).EnumerateArray());
        var started = await app.Client.PostAsync($"/api/libraries/{libraryId}/import-existing", content: null);
        var runId = (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("run").GetProperty("id").GetString()!;
        var runs = app.Services.GetRequiredService<ILibraryImportRunsRepository>();
        await runs.RecordIssueAsync(runId, libraryId, @"D:\Films\Unknown Thing", "unmatched", "No title in the folder name could be matched.", CancellationToken.None);
        await runs.RecordIssueAsync(runId, libraryId, @"D:\Films\Another", "ambiguous", "Two films fit this folder.", CancellationToken.None);

        var issues = (await app.Client.GetFromJsonAsync<JsonElement>($"/api/libraries/{libraryId}/import-existing/issues")).EnumerateArray().ToList();
        var one = (await app.Client.GetFromJsonAsync<JsonElement>($"/api/libraries/{libraryId}/import-existing/issues?take=1")).EnumerateArray().ToList();

        Assert.Equal([@"D:\Films\Unknown Thing", @"D:\Films\Another"], issues.Select(issue => issue.GetProperty("sourcePath").GetString()));
        Assert.Equal("unmatched", issues[0].GetProperty("kind").GetString());
        Assert.Equal("Two films fit this folder.", issues[1].GetProperty("detail").GetString());
        Assert.Single(one);
    }
}
