using System.Net.Http.Json;
using Deluno.Filesystem;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// "Mark missing" on a file that is really gone from a real folder: Activity gets a named line saying Deluno stopped wanting
/// the title (and the file is never deleted), and a repair that matched nothing leaves a named failure line instead. Refs #1296.
/// </summary>
public sealed class MarkMissingSaysWhatItDidInActivityOnRealFilesTests
{
    [Fact]
    public async Task A_repair_that_marked_a_file_missing_writes_the_stopped_wanting_line_and_one_that_matched_nothing_writes_a_failure_line()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var gone = await ApiScenes.FilmOnDiskAsync(app, "Arrival", 2016, libraryId);
        var kept = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021, libraryId);
        File.Delete(gone.FilePath);
        var issueId = FilesystemIssueIds.Encode("missingTrackedFile", "movies", libraryId, gone.MovieId, null, gone.FilePath);

        (await app.Client.PostAsJsonAsync("/api/filesystem/reconciliation/repair", new { issueId, action = "mark-missing" })).EnsureSuccessStatusCode();
        var first = await app.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(50, "movie", gone.MovieId, CancellationToken.None);
        var said = Assert.Single(first, item => item.Category == "filesystem.reconciliation.unmonitored");
        Assert.Contains("Arrival", said.Message);
        Assert.Contains("stopped wanting it", said.Message);

        // The same repair again finds nothing to change and says so under its own name.
        await app.Client.PostAsJsonAsync("/api/filesystem/reconciliation/repair", new { issueId, action = "mark-missing" });
        var second = await app.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(50, "movie", gone.MovieId, CancellationToken.None);
        var failed = Assert.Single(second, item => item.Category == "filesystem.reconciliation.repair-failed");
        Assert.Contains("failed", failed.Message);
        Assert.Single(second, item => item.Category == "filesystem.reconciliation.unmonitored");
        Assert.True(File.Exists(kept.FilePath), "no other file is touched");
    }
}
