using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Import-existing is a tracked background run, never inline work: the start answers 202 with the run's progress, the first
/// slice is queued under the run's continuation key, and resume only means something from a paused run (coverage register, engines).
/// Refs #1296.
/// </summary>
public sealed class TheExistingMediaImportIsAQueuedRunTests
{
    private static async Task<(string LibraryId, string File)> LibraryWithAFilmAsync(ApplicationTestHost app)
    {
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var root = await ApiScenes.LibraryRootAsync(app, libraryId);
        var file = Path.Combine(root, "Dune (2021)", "Dune (2021) Bluray-1080p.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllBytesAsync(file, [1, 2, 3]);
        return (libraryId, file);
    }

    [Fact]
    public async Task Starting_answers_202_with_the_run_queues_one_slice_under_the_runs_continuation_key_and_imports_nothing_inline()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (libraryId, file) = await LibraryWithAFilmAsync(app);

        var response = await app.Client.PostAsync($"/api/libraries/{libraryId}/import-existing", content: null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var run = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("run");
        var runId = run.GetProperty("id").GetString()!;
        Assert.Equal("queued", run.GetProperty("status").GetString());
        Assert.Equal(0, run.GetProperty("processedCount").GetInt32());
        Assert.Equal(1, await ApiScenes.ScalarAsync(app, "jobs", "SELECT COUNT(*) FROM job_queue WHERE job_type = 'library.import.existing';"));
        Assert.Equal($"library.import.existing:{runId}:0", await ApiScenes.TextAsync(app, "jobs", "SELECT dedupe_key FROM job_queue WHERE job_type = 'library.import.existing';"));
        Assert.Equal(0, await ApiScenes.ScalarAsync(app, "movies", "SELECT COUNT(*) FROM movie_entries;"));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Resume_works_from_a_paused_run_only_and_every_other_state_answers_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (libraryId, _) = await LibraryWithAFilmAsync(app);

        // Nothing has been started yet: no run to resume.
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync($"/api/libraries/{libraryId}/import-existing/resume", content: null)).StatusCode);

        await app.Client.PostAsync($"/api/libraries/{libraryId}/import-existing", content: null);
        // Queued is not paused.
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync($"/api/libraries/{libraryId}/import-existing/resume", content: null)).StatusCode);

        Assert.Equal(HttpStatusCode.Accepted, (await app.Client.PostAsync($"/api/libraries/{libraryId}/import-existing/pause", content: null)).StatusCode);
        var jobsBefore = await ApiScenes.ScalarAsync(app, "jobs", "SELECT COUNT(*) FROM job_queue WHERE job_type = 'library.import.existing';");
        var resumed = await app.Client.PostAsync($"/api/libraries/{libraryId}/import-existing/resume", content: null);
        Assert.Equal(HttpStatusCode.Accepted, resumed.StatusCode);
        Assert.Equal("running", (await resumed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("run").GetProperty("status").GetString());
        // The same continuation key is already waiting, so the next slice is the one queued, not a duplicate.
        Assert.Equal(jobsBefore, await ApiScenes.ScalarAsync(app, "jobs", "SELECT COUNT(*) FROM job_queue WHERE job_type = 'library.import.existing';"));

        // Running is not paused either.
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync($"/api/libraries/{libraryId}/import-existing/resume", content: null)).StatusCode);
    }
}
