using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The edges of bringing in what a library folder already holds: how many a preview page and a chosen import will take, that the
/// same path chosen twice is imported once, that starting twice is one run that says it started, what the progress read answers
/// for an active, a finished and a never-started library, and which states can be paused.
/// </summary>
public sealed class TheExistingImportRunEdgesTests
{
    private static async Task<string> LibraryWithFilmsAsync(ApplicationTestHost app, int count)
    {
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var root = await ApiScenes.LibraryRootAsync(app, libraryId);
        for (var number = 1; number <= count; number++)
        {
            var file = Path.Combine(root, $"Film {number:000} ({1900 + number})", $"Film {number:000} ({1900 + number}) Bluray-1080p.mkv");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllBytesAsync(file, [1, 2, 3]);
        }

        return libraryId;
    }

    private static async Task<int> StartLinesAsync(ApplicationTestHost app)
    {
        var activity = await app.Client.GetStringAsync("/api/activity");
        return System.Text.RegularExpressions.Regex.Matches(activity, "started bringing in what is already in").Count;
    }

    [Fact]
    public async Task A_preview_page_holds_fifty_by_default_and_never_fewer_than_one_or_more_than_a_hundred()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await LibraryWithFilmsAsync(app, 105);

        async Task<int> ItemsAsync(string query)
            => (await app.Client.GetFromJsonAsync<JsonElement>($"/api/libraries/{libraryId}/import-existing/preview{query}")).GetProperty("items").GetArrayLength();

        Assert.Equal(50, await ItemsAsync(""));
        Assert.Equal(1, await ItemsAsync("?take=0"));
        Assert.Equal(1, await ItemsAsync("?take=-7"));
        Assert.Equal(100, await ItemsAsync("?take=100"));
        Assert.Equal(100, await ItemsAsync("?take=5000"));
        Assert.Equal(7, await ItemsAsync("?take=7"));
    }

    [Fact]
    public async Task A_chosen_import_takes_at_most_a_hundred_paths_and_the_same_path_chosen_twice_in_any_letter_case_is_imported_once()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await LibraryWithFilmsAsync(app, 105);
        var root = await ApiScenes.LibraryRootAsync(app, libraryId);
        var every = Directory.GetDirectories(root).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        Assert.Equal(105, every.Length);

        var many = await app.Client.PostAsJsonAsync($"/api/libraries/{libraryId}/import-existing/selected", new { sourcePaths = every });
        many.EnsureSuccessStatusCode();
        var capped = await many.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(100, capped.GetProperty("requestedCount").GetInt32());
        Assert.Equal(100, capped.GetProperty("importedCount").GetInt32());

        var rest = every[100..];
        var twice = await app.Client.PostAsJsonAsync($"/api/libraries/{libraryId}/import-existing/selected",
            new { sourcePaths = new[] { rest[0], rest[0].ToUpperInvariant(), rest[0], rest[1] } });
        twice.EnsureSuccessStatusCode();
        var collapsed = await twice.Content.ReadFromJsonAsync<JsonElement>();
        // Collapsed before anything is checked: the two films are brought in once each, and nothing is set aside for the repeats.
        Assert.Equal(2, collapsed.GetProperty("importedCount").GetInt32());
        Assert.Empty(collapsed.GetProperty("issues").EnumerateArray());
        Assert.Equal(102, await ApiScenes.ScalarAsync(app, "movies", "SELECT COUNT(*) FROM movie_entries;"));
    }

    [Fact]
    public async Task Starting_twice_is_one_run_that_says_it_started_only_while_nothing_has_been_processed_and_the_progress_read_follows_the_run()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await LibraryWithFilmsAsync(app, 2);

        // No run yet.
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync($"/api/libraries/{libraryId}/import-existing")).StatusCode);

        var first = await (await app.Client.PostAsync($"/api/libraries/{libraryId}/import-existing", content: null)).Content.ReadFromJsonAsync<JsonElement>();
        var second = await (await app.Client.PostAsync($"/api/libraries/{libraryId}/import-existing", content: null)).Content.ReadFromJsonAsync<JsonElement>();
        var runId = first.GetProperty("run").GetProperty("id").GetString();
        Assert.Equal(runId, second.GetProperty("run").GetProperty("id").GetString());
        Assert.Equal(1, await ApiScenes.ScalarAsync(app, "platform", "SELECT COUNT(*) FROM library_import_runs;"));
        Assert.Equal(2, await StartLinesAsync(app));

        // The run is part-way: starting again hands back the same run and says nothing new.
        await ApiScenes.ExecuteAsync(app, "platform", "UPDATE library_import_runs SET processed_count = 5, estimated_total = 10, status = 'running';");
        var active = await app.Client.GetFromJsonAsync<JsonElement>($"/api/libraries/{libraryId}/import-existing");
        Assert.Equal(runId, active.GetProperty("run").GetProperty("id").GetString());
        Assert.Equal(50, active.GetProperty("percentComplete").GetInt32());
        var again = await (await app.Client.PostAsync($"/api/libraries/{libraryId}/import-existing", content: null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(runId, again.GetProperty("run").GetProperty("id").GetString());
        Assert.Equal(2, await StartLinesAsync(app));

        // Finished, the read answers with the latest run.
        await ApiScenes.ExecuteAsync(app, "platform", "UPDATE library_import_runs SET status = 'completed', processed_count = 10;");
        var latest = await app.Client.GetFromJsonAsync<JsonElement>($"/api/libraries/{libraryId}/import-existing");
        Assert.Equal(runId, latest.GetProperty("run").GetProperty("id").GetString());
        Assert.Equal("completed", latest.GetProperty("run").GetProperty("status").GetString());
        Assert.Equal(100, latest.GetProperty("percentComplete").GetInt32());
    }

    [Fact]
    public async Task Only_a_queued_or_running_run_can_be_paused_and_anything_else_is_a_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await LibraryWithFilmsAsync(app, 1);

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync($"/api/libraries/{libraryId}/import-existing/pause", content: null)).StatusCode);

        await app.Client.PostAsync($"/api/libraries/{libraryId}/import-existing", content: null);
        var paused = await app.Client.PostAsync($"/api/libraries/{libraryId}/import-existing/pause", content: null);
        Assert.Equal(HttpStatusCode.Accepted, paused.StatusCode);
        Assert.Equal("paused", (await paused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("run").GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync($"/api/libraries/{libraryId}/import-existing/pause", content: null)).StatusCode);

        await ApiScenes.ExecuteAsync(app, "platform", "UPDATE library_import_runs SET status = 'running';");
        Assert.Equal(HttpStatusCode.Accepted, (await app.Client.PostAsync($"/api/libraries/{libraryId}/import-existing/pause", content: null)).StatusCode);

        await ApiScenes.ExecuteAsync(app, "platform", "UPDATE library_import_runs SET status = 'completed';");
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync($"/api/libraries/{libraryId}/import-existing/pause", content: null)).StatusCode);
    }
}
