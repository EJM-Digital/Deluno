using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// "These files really are gone" against a real library folder: the note is raised by the real file check when a large share of
/// a library is missing at once, and the answer marks what is still missing as gone without ever touching a file on disk.
/// </summary>
public sealed class FilesGoneOnRealFilesTests
{
    private const int Films = 24;
    private const int Deleted = 22;

    [Fact]
    public async Task A_library_with_no_note_answers_404_and_nothing_changes()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (libraryId, files, ids) = await SeedAsync(app);

        using var response = await app.Client.PostAsync($"/api/filesystem/libraries/{libraryId}/files-gone", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.All(files, file => Assert.True(File.Exists(file)));
        Assert.All(ids, id => Assert.True(MovieHasFile(app, id).GetAwaiter().GetResult()));
    }

    [Fact]
    public async Task A_big_dropout_raises_the_note_and_changes_nothing_until_the_person_confirms()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (_, files, ids) = await SeedAsync(app);
        foreach (var file in files.Take(Deleted)) File.Delete(file);

        using var check = await app.Client.PostAsync("/api/filesystem/file-check", content: null);

        Assert.Equal(HttpStatusCode.OK, check.StatusCode);
        var health = await app.Client.GetStringAsync("/api/system/health");
        Assert.Contains($"{Deleted} of {Films} files could not be found, so Deluno changed nothing.", health);
        Assert.All(ids, id => Assert.True(MovieHasFile(app, id).GetAwaiter().GetResult(), "one pass must not unmonitor or unlink anything"));
        Assert.Equal(Films - Deleted, files.Count(File.Exists));
    }

    [Fact]
    public async Task Confirming_marks_only_the_missing_files_as_gone_leaves_the_files_that_are_there_and_writes_one_activity_line()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (libraryId, files, ids) = await SeedAsync(app);
        foreach (var file in files.Take(Deleted)) File.Delete(file);
        (await app.Client.PostAsync("/api/filesystem/file-check", content: null)).EnsureSuccessStatusCode();
        // One of the missing files comes back before the person answers.
        File.WriteAllText(files[0], "it came back");

        using var response = await app.Client.PostAsync($"/api/filesystem/libraries/{libraryId}/files-gone", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(Deleted - 1, body.GetProperty("markedGone").GetInt32());
        Assert.False(body.GetProperty("libraryUnreachable").GetBoolean());
        Assert.False(body.GetProperty("inBackground").GetBoolean());
        Assert.True(await MovieHasFile(app, ids[0]), "a file that came back is left alone");
        for (var i = 1; i < Deleted; i++) Assert.False(await MovieHasFile(app, ids[i]), $"film {i} was gone");
        for (var i = Deleted; i < Films; i++) Assert.True(await MovieHasFile(app, ids[i]), $"film {i} is still on disk");
        // Disk: Deluno deleted nothing and created nothing.
        Assert.Equal(Films - Deleted + 1, files.Count(File.Exists));
        Assert.Equal("it came back", await File.ReadAllTextAsync(files[0]));
        var activity = await app.Client.GetStringAsync("/api/activity?take=100");
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(activity, "library\\.files-gone\""));
    }

    [Fact]
    public async Task Confirming_while_the_library_folder_is_still_missing_changes_nothing_and_says_so()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (libraryId, files, ids) = await SeedAsync(app);
        foreach (var file in files.Take(Deleted)) File.Delete(file);
        (await app.Client.PostAsync("/api/filesystem/file-check", content: null)).EnsureSuccessStatusCode();
        // The whole drive drops out: a library that cannot be read proves nothing about its files.
        Directory.Delete(Path.Combine(app.DataRoot, "movies"), recursive: true);

        using var response = await app.Client.PostAsync($"/api/filesystem/libraries/{libraryId}/files-gone", content: null);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("libraryUnreachable").GetBoolean());
        Assert.Equal(0, body.GetProperty("markedGone").GetInt32());
        Assert.Contains("is still not reachable, so Deluno changed nothing.", body.GetProperty("message").GetString());
        Assert.All(ids, id => Assert.True(MovieHasFile(app, id).GetAwaiter().GetResult()));
    }

    [Fact]
    public async Task Above_the_threshold_the_answer_runs_in_the_background_says_so_with_202_and_still_marks_only_what_is_missing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (libraryId, files, ids) = await SeedAsync(app);
        foreach (var file in files.Take(Deleted)) File.Delete(file);
        // A dropout of thousands of files: the note carries the count the file check found, which is what picks the background path.
        await app.Services.GetRequiredService<Deluno.Contracts.IHealthCheckRepository>().UpsertResultsAsync(
            [Deluno.Contracts.HealthCheckOutcome.Problem(
                $"notice:library-unreachable:{libraryId}", Deluno.Contracts.HealthCheckCategories.Notice, "Films looks unreachable", "Most files could not be found.",
                cause: null, fix: null, detailsJson: JsonSerializer.Serialize(new { libraryId, missing = 1500, total = 1520, raisedUtc = DateTimeOffset.UtcNow }))],
            CancellationToken.None);

        using var response = await app.Client.PostAsync($"/api/filesystem/libraries/{libraryId}/files-gone", content: null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("inBackground").GetBoolean());
        Assert.Contains("marking about 1500 files as gone in the background", body.GetProperty("message").GetString());
        // The work finishes on its own: the files that really are missing are marked gone, the ones on disk are left alone, and no file is touched.
        var finished = false;
        for (var attempt = 0; attempt < 200 && !finished; attempt++)
        {
            finished = !await MovieHasFile(app, ids[0]);
            if (!finished) await Task.Delay(50); // reason: the background job has no completion signal; this polls its visible effect, up to 10 seconds
        }
        Assert.True(finished, "the background job never marked the missing files as gone");
        for (var i = 0; i < Deleted; i++) Assert.False(await MovieHasFile(app, ids[i]), $"film {i} was gone");
        for (var i = Deleted; i < Films; i++) Assert.True(await MovieHasFile(app, ids[i]), $"film {i} is still on disk");
        Assert.Equal(Films - Deleted, files.Count(File.Exists));
    }

    [Fact]
    public async Task A_second_click_while_the_background_run_is_still_going_answers_202_already_marking_and_changes_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (libraryId, files, ids) = await SeedAsync(app);
        foreach (var file in files.Take(Deleted)) File.Delete(file);
        await app.Services.GetRequiredService<Deluno.Contracts.IHealthCheckRepository>().UpsertResultsAsync(
            [Deluno.Contracts.HealthCheckOutcome.Problem(
                $"notice:library-unreachable:{libraryId}", Deluno.Contracts.HealthCheckCategories.Notice, "Films looks unreachable", "Most files could not be found.",
                cause: null, fix: null, detailsJson: JsonSerializer.Serialize(new { libraryId, missing = 1500, total = 1520, raisedUtc = DateTimeOffset.UtcNow }))],
            CancellationToken.None);
        // A run is in progress for this library: the route's own record of that, the one the first click sets and its job clears.
        var running = (System.Collections.Concurrent.ConcurrentDictionary<string, byte>)typeof(Deluno.Filesystem.FilesystemEndpointRouteBuilderExtensions)
            .GetField("FilesGoneRunning", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        Assert.True(running.TryAdd(libraryId, 0));
        try
        {
            using var response = await app.Client.PostAsync($"/api/filesystem/libraries/{libraryId}/files-gone", content: null);

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Deluno is already marking these files as gone. This note goes away when it is done.", body.GetProperty("message").GetString());
            Assert.True(body.GetProperty("inBackground").GetBoolean());
            Assert.Equal(0, body.GetProperty("markedGone").GetInt32());
            // Nothing was started, so nothing changed.
            Assert.All(ids, id => Assert.True(MovieHasFile(app, id).GetAwaiter().GetResult()));
        }
        finally
        {
            running.TryRemove(libraryId, out _);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<bool> MovieHasFile(ApplicationTestHost app, string id)
        => (await app.Services.GetRequiredService<IMovieCatalogRepository>().GetByIdAsync(id, CancellationToken.None))?.HasFile ?? false;

    private static async Task<(string LibraryId, List<string> Files, List<string> Ids)> SeedAsync(ApplicationTestHost app)
    {
        var root = Path.Combine(app.DataRoot, "movies");
        Directory.CreateDirectory(root);
        var created = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name = "Films",
            mediaType = "movies",
            purpose = "collection",
            rootPath = root,
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
            searchIntervalMinutes = (int?)null,
            retryDelayHours = (int?)null,
            maxItemsPerRun = (int?)null
        });
        created.EnsureSuccessStatusCode();
        var libraryId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var files = new List<string>();
        var ids = new List<string>();
        for (var i = 0; i < Films; i++)
        {
            var title = $"Film {(char)('A' + i)}";
            var year = 1950 + i;
            var folder = Directory.CreateDirectory(Path.Combine(root, $"{title} ({year})")).FullName;
            var file = Path.Combine(folder, $"{title.Replace(' ', '.')}.{year}.1080p.BluRay.x264-HD4U.mkv");
            await File.WriteAllTextAsync(file, "not really a film " + i);
            var added = await app.Client.PostAsJsonAsync("/api/movies/", new { title, releaseYear = year, monitored = true, libraryId, searchForMissingNow = false });
            Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync());
            var id = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
            Assert.True(await MovieHasFile(app, id), $"{title} should have linked the file already in its folder");
            files.Add(file);
            ids.Add(id);
        }

        return (libraryId, files, ids);
    }
}
