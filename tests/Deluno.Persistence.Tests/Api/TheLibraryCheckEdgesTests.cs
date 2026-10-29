using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Filesystem;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The edges of the library check: what it lists comes critical first and then by library, kind and path with its totals; a
/// repair action that does not belong to the issue is refused in plain words; and a rescan of a title with no file on record
/// says there was nothing to check.
/// </summary>
public sealed class TheLibraryCheckEdgesTests
{
    private static async Task<string> LibraryWithVideosAsync(ApplicationTestHost app, string name, string folder, params string[] videos)
    {
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies", folder, name);
        var root = await ApiScenes.LibraryRootAsync(app, libraryId);
        foreach (var video in videos)
        {
            await File.WriteAllBytesAsync(Path.Combine(root, video), [1, 2, 3]);
        }

        return libraryId;
    }

    [Fact]
    public async Task The_check_lists_a_missing_library_first_then_by_library_kind_and_path_and_counts_what_it_looked_at()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await LibraryWithVideosAsync(app, "Beta", "BetaFilms", "c.mkv");
        await LibraryWithVideosAsync(app, "Alpha", "AlphaFilms", "b.mkv", "a.mkv");
        var gone = await ApiScenes.CreateLibraryAsync(app, "tv", "ZetaShows", "Zeta");
        Directory.Delete(await ApiScenes.LibraryRootAsync(app, gone));

        var report = await app.Services.GetRequiredService<IFilesystemReconciliationService>().ScanAsync(CancellationToken.None);

        Assert.Equal(3, report.LibraryCount);
        Assert.Equal(report.Issues.Count, report.IssueCount);
        Assert.Equal(
            [("Zeta", "libraryRootUnreachable", "critical"), ("Alpha", "orphanFile", "warning"), ("Alpha", "orphanFile", "warning"), ("Beta", "orphanFile", "warning")],
            report.Issues.Select(issue => (issue.LibraryName, issue.Kind, issue.Severity)));
        Assert.Equal(["a.mkv", "b.mkv", "c.mkv"], report.Issues.Skip(1).Select(issue => Path.GetFileName(issue.Path)));
    }

    [Fact]
    public async Task A_repair_action_that_does_not_exist_for_an_issue_is_refused_as_not_supported_and_changes_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await LibraryWithVideosAsync(app, "Alpha", "AlphaFilms", "a.mkv");
        var service = app.Services.GetRequiredService<IFilesystemReconciliationService>();
        var issue = Assert.Single((await service.ScanAsync(CancellationToken.None)).Issues);

        var result = await service.RepairAsync(new FilesystemReconciliationRepairRequest(issue.Id, "make-it-so"), CancellationToken.None);

        Assert.False(result.Repaired);
        Assert.Equal("This repair action is not supported for the selected reconciliation issue.", result.Message);
        Assert.True(File.Exists(issue.Path));
    }

    [Fact]
    public async Task Rescanning_a_title_with_no_file_on_record_says_there_was_nothing_to_check_and_changes_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        var added = await movies.AddAsync(new CreateMovieRequest("Not Here Yet", 2020, null), CancellationToken.None);
        await movies.EnsureWantedStateAsync(added.Id, libraryId, "missing", "Wanted", hasFile: false, null, "WEB 1080p", false, CancellationToken.None);

        var response = await app.Client.PostAsJsonAsync("/api/filesystem/rescan-title", new { mediaType = "movies", titleId = added.Id });

        Assert.True(response.IsSuccessStatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("nothing-tracked", result.GetProperty("outcome").GetString());
        Assert.Equal(0, result.GetProperty("filesChecked").GetInt32());
        Assert.Equal("Not Here Yet has no file on record, so there was nothing to check.", result.GetProperty("summary").GetString());
        Assert.True((await movies.GetByIdAsync(added.Id, CancellationToken.None))!.Monitored);
    }
}
