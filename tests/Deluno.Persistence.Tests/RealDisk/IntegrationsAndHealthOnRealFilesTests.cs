using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Worker.HealthChecks.Checks;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// What other programs read from Deluno, and what Deluno's own health checks say about the disk, asked of a real host over a
/// real temp folder and real SQLite files: the Home Assistant sensors against a live summary, the Home Assistant key against
/// every command it needs, the filesystem routes against a key that may only read, the library-root check against real
/// folders (there, gone, refusing writes), and the lost-hardlink check against real hard links.
/// </summary>
public sealed class IntegrationsAndHealthOnRealFilesTests
{
    private static async Task<HttpClient> KeyClientAsync(ApplicationTestHost app, string scopes)
    {
        var created = await app.Client.PostAsJsonAsync("/api/api-keys/", new { name = "Scope " + scopes, scopes });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var client = app.CreateAnonymousClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!);
        return client;
    }

    private static async Task<string> CreateLibraryAsync(ApplicationTestHost app, string root, string? downloads = null)
    {
        Directory.CreateDirectory(root);
        var created = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name = "Movies", mediaType = "movies", purpose = "collection", rootPath = root, downloadsPath = downloads, qualityProfileId = (string?)null,
            importWorkflow = "copy", processorName = (string?)null, processorOutputPath = (string?)null, processorTimeoutMinutes = (int?)null,
            processorFailureMode = (string?)null, autoSearchEnabled = false, missingSearchEnabled = false, upgradeSearchEnabled = false,
            searchIntervalMinutes = (int?)null, retryDelayHours = (int?)null, maxItemsPerRun = (int?)null
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Deluno.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    // ------------------------------------------------------------------ the summary Home Assistant reads

    [Fact]
    public async Task On_an_install_with_nothing_wrong_the_summary_has_every_name_the_sensor_reads_and_every_count_is_zero()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        Directory.CreateDirectory(Path.Combine(app.DataRoot, "movies"));

        var summary = await app.Client.GetFromJsonAsync<JsonElement>("/api/automation/summary");

        Assert.True(summary.TryGetProperty("generatedUtc", out _));
        foreach (var name in new[] { "status", "ready", "failedChecks" }) Assert.True(summary.GetProperty("readiness").TryGetProperty(name, out _), name);
        foreach (var name in new[] { "active", "queued", "failed", "openDispatchAlerts" }) Assert.True(summary.GetProperty("queue").TryGetProperty(name, out _), name);
        foreach (var name in new[] { "active", "failed", "completed", "issues" }) Assert.Equal(0, summary.GetProperty("imports").GetProperty(name).GetInt32());
        Assert.Equal(0, summary.GetProperty("queue").GetProperty("failed").GetInt32());
        Assert.Equal(0, summary.GetProperty("queue").GetProperty("openDispatchAlerts").GetInt32());
        Assert.DoesNotContain(summary.GetProperty("attention").EnumerateArray(), item => item.GetProperty("code").GetString() == "library-import.failed");
        Assert.Empty(RealDiskLibrary.FilesUnder(Path.Combine(app.DataRoot, "movies")));
    }

    [Fact]
    public async Task The_summary_counts_a_failed_job_an_open_download_problem_and_a_failed_import_under_the_names_the_sensor_reads()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Path.Combine(app.DataRoot, "movies");
        Directory.CreateDirectory(Path.Combine(root, "Nosferatu (1922)"));
        File.WriteAllBytes(Path.Combine(root, "Nosferatu (1922)", "Nosferatu.1922.1080p.BluRay.x264-HD4U.mkv"), [1, 2, 3, 4]);
        var libraryId = await CreateLibraryAsync(app, root);

        using (var scope = app.Services.CreateScope())
        {
            var services = scope.ServiceProvider;
            var import = services.GetRequiredService<IExistingLibraryImportService>();
            var started = await import.StartAsync(libraryId, CancellationToken.None);
            Directory.Delete(root, recursive: true);
            await import.RunSliceAsync(started!.Run.Id, CancellationToken.None);

            var job = await services.GetRequiredService<IJobScheduler>().EnqueueAsync(
                new EnqueueJobRequest(JobType: "test.job", Source: "test", PayloadJson: "{}", RelatedEntityType: null, RelatedEntityId: null), CancellationToken.None);
            var dispatchId = await services.GetRequiredService<IJobQueueRepository>().RecordDownloadDispatchAsync(
                libraryId, "movies", "movie", Guid.NewGuid().ToString("N"), "Nosferatu 1922 1080p", "Indexer", "client-1", "Client", "sent", null,
                cancellationToken: CancellationToken.None);
            await services.GetRequiredService<IDispatchProblemLog>().RecordAsync(dispatchId, "grab-timeout", CancellationToken.None);
            await using var connection = await services.GetRequiredService<Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory>().OpenConnectionAsync("jobs", CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE job_queue SET status = 'failed', last_error = 'It broke.' WHERE id = '{job.Id}';";
            await command.ExecuteNonQueryAsync();
        }

        var summary = await app.Client.GetFromJsonAsync<JsonElement>("/api/automation/summary");

        Assert.Equal(1, summary.GetProperty("queue").GetProperty("failed").GetInt32());
        Assert.Equal(1, summary.GetProperty("queue").GetProperty("openDispatchAlerts").GetInt32());
        Assert.Equal(1, summary.GetProperty("imports").GetProperty("failed").GetInt32());
        var attention = summary.GetProperty("attention").EnumerateArray().ToArray();
        Assert.Contains(attention, item => item.GetProperty("code").GetString() == "library-import.failed" && item.GetProperty("severity").GetString() == "error");
        Assert.All(attention, item =>
        {
            foreach (var name in new[] { "code", "severity", "summary", "details", "detectedUtc" }) Assert.True(item.TryGetProperty(name, out _), name);
        });
    }

    /// <summary>Every field the Home Assistant recipe reads must be in what the live summary answers, under that name.</summary>
    [Fact]
    public async Task Every_field_the_Home_Assistant_sensors_read_is_in_the_live_summary_and_the_sensor_asks_the_route_the_server_answers()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var recipe = File.ReadAllText(Path.Combine(RepositoryRoot(), "integrations", "home-assistant", "deluno.yaml"));
        var resource = Regex.Match(recipe, @"resource:\s*""\{\{[^}]*\}\}(?<path>/api/v1/[^""]+)""").Groups["path"].Value;
        var jsonAttributes = Regex.Matches(recipe[recipe.IndexOf("json_attributes:", StringComparison.Ordinal)..].Split("\ntemplate:")[0], @"^\s+-\s+(?<name>\w+)\s*$", RegexOptions.Multiline).Select(m => m.Groups["name"].Value).ToArray();
        var valuePath = Regex.Match(recipe, @"value_template:\s*""\{\{\s*value_json\.(?<path>[\w.]+)\s*\}\}""").Groups["path"].Value;
        var templateReads = Regex.Matches(recipe, @"state_attr\('sensor\.deluno',\s*'(?<group>\w+)'\).*?\.get\('(?<field>\w+)'").Select(m => (Group: m.Groups["group"].Value, Field: m.Groups["field"].Value)).ToArray();
        Assert.Equal("/api/v1/automation/summary", resource);
        Assert.Equal(["readiness", "queue", "imports", "attention"], jsonAttributes);
        Assert.Equal(5, templateReads.Length);

        // The shipped server answers the same route without its version prefix (the version alias is added by the full pipeline).
        var key = await KeyClientAsync(app, "home-assistant");
        using var response = await key.GetAsync(resource.Replace("/api/v1/", "/api/"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<JsonElement>();

        foreach (var attribute in jsonAttributes) Assert.True(summary.TryGetProperty(attribute, out _), $"the summary has no '{attribute}' the sensor lists");
        var state = valuePath.Split('.').Aggregate(summary, (element, name) => element.GetProperty(name));
        Assert.Equal(JsonValueKind.String, state.ValueKind);
        foreach (var (group, field) in templateReads)
        {
            Assert.True(summary.GetProperty(group).TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number, $"the summary has no number '{group}.{field}' a template sensor reads");
        }
    }

    // ------------------------------------------------------------------ the Home Assistant key

    [Fact]
    public async Task A_key_made_from_the_Home_Assistant_template_reaches_the_summary_and_every_command_in_the_recipe_but_not_backups_or_keys()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await CreateLibraryAsync(app, Path.Combine(app.DataRoot, "Films"));
        var home = await KeyClientAsync(app, "home-assistant");

        async Task<HttpStatusCode> SendAsync(HttpMethod method, string url, object body)
        {
            using var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
            using var response = await home.SendAsync(request);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, (await home.GetAsync("/api/automation/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, await SendAsync(HttpMethod.Post, $"/api/libraries/{libraryId}/search-now", new { }));
        Assert.Equal(HttpStatusCode.OK, await SendAsync(HttpMethod.Put, "/api/settings/automation", new { isEnabled = false }));
        Assert.Equal(HttpStatusCode.OK, await SendAsync(HttpMethod.Put, "/api/settings/automation", new { isEnabled = true }));
        foreach (var url in new[] { $"/api/libraries/{libraryId}/import-existing/pause", $"/api/libraries/{libraryId}/import-existing/resume", "/api/intake-sources/none/approve-preview" })
        {
            var status = await SendAsync(HttpMethod.Post, url, new { });
            Assert.True(status is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden), $"{url} refused the Home Assistant key with {(int)status}");
            Assert.True((int)status < 500, $"{url} failed with {(int)status}");
        }

        // The template does not include system, so a key made from it cannot make a backup or touch the keys.
        Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(HttpMethod.Post, "/api/backups", new { reason = "x" }));
        Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(HttpMethod.Post, "/api/api-keys/", new { name = "x", scopes = "all" }));
        Assert.Empty(Directory.GetFiles(app.DataRoot, "*.zip", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Revoking_a_key_that_is_unknown_or_already_revoked_answers_404_and_the_revoked_key_no_longer_opens_anything()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var created = await app.Client.PostAsJsonAsync("/api/api-keys/", new { name = "Short lived", scopes = "read" });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("item").GetProperty("id").GetString()!;

        var unknown = await app.Client.DeleteAsync("/api/api-keys/nope");
        var first = await app.Client.DeleteAsync($"/api/api-keys/{id}");
        var second = await app.Client.DeleteAsync($"/api/api-keys/{id}");

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        Assert.Equal(0L, Convert.ToInt64(await ScalarAsync(app, "platform", $"SELECT COUNT(*) FROM api_keys WHERE id = '{id}';")));
    }

    private static async Task<object?> ScalarAsync(ApplicationTestHost app, string database, string sql)
    {
        var factory = app.Services.GetRequiredService<Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory>();
        await using var connection = await factory.OpenConnectionAsync(database, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    // ------------------------------------------------------------------ the filesystem routes need the Imports scope

    [Fact]
    public async Task A_read_only_key_cannot_delete_leftover_files_and_an_imports_key_can_browse_folders()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var downloads = Directory.CreateDirectory(Path.Combine(app.DataRoot, "downloads")).FullName;
        await CreateLibraryAsync(app, root, downloads);
        var leftover = Path.Combine(downloads, "Show.S01E01.leftover.mkv");
        File.WriteAllBytes(leftover, [1, 2, 3]);
        var read = await KeyClientAsync(app, "read");
        var imports = await KeyClientAsync(app, "read,imports");

        var deleteRead = await read.PostAsJsonAsync("/api/filesystem/import/season-pack/leftovers/delete", new { paths = new[] { leftover } });
        var browseImports = await imports.GetAsync($"/api/filesystem/directories?path={Uri.EscapeDataString(root)}");

        Assert.Equal(HttpStatusCode.Forbidden, deleteRead.StatusCode);
        Assert.True(File.Exists(leftover), "a refused delete leaves the file where it is");
        Assert.Equal(HttpStatusCode.OK, browseImports.StatusCode);
    }

    // DEFECT (observed): the filesystem group is meant to need the Imports policy so a read-only key cannot browse, but a key
    // holding only `read` is answered 200 with the machine's folders by GET /api/filesystem/directories.
    [Fact]
    public async Task A_read_only_key_cannot_browse_the_servers_folders()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var read = await KeyClientAsync(app, "read");

        var browseRead = await read.GetAsync($"/api/filesystem/directories?path={Uri.EscapeDataString(root)}");

        Assert.Equal(HttpStatusCode.Forbidden, browseRead.StatusCode);
    }

    // ------------------------------------------------------------------ the library root check, on real folders

    private static LibraryRootHealthCheck RootCheck(ApplicationTestHost app)
        => new(app.Services.GetRequiredService<ILibrariesRepository>(), app.Services.GetRequiredService<ILibraryAvailabilityService>());

    private static string[] ProbeLeftovers(string root) => Directory.GetFiles(root, ".deluno-health-*.tmp", SearchOption.AllDirectories);

    [Fact]
    public async Task A_library_root_that_is_there_and_writable_reads_OK_and_leaves_no_probe_file_behind()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Path.Combine(app.DataRoot, "Films");
        var libraryId = await CreateLibraryAsync(app, root);

        var outcome = Assert.Single(await RootCheck(app).RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Ok, outcome.Status);
        Assert.Equal($"library:{libraryId}:root", outcome.Id);
        Assert.Contains("reachable and writable", outcome.Message);
        Assert.Contains("\"writable\":true", outcome.DetailsJson);
        Assert.Empty(ProbeLeftovers(root));
    }

    [Fact]
    public async Task A_library_root_that_has_gone_reads_as_a_problem_naming_the_drive_or_share()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Path.Combine(app.DataRoot, "Films");
        await CreateLibraryAsync(app, root);
        Directory.Delete(root, recursive: true);

        var outcome = Assert.Single(await RootCheck(app).RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Contains("is not reachable", outcome.Message);
        Assert.Contains(root, outcome.Message);
        Assert.Contains("check that the drive or share is mounted", outcome.Cause);
        Assert.Contains("\"reachable\":false", outcome.DetailsJson);
    }

    [Fact]
    public async Task A_library_root_that_refuses_writes_reads_as_a_problem_and_the_probe_leaves_nothing_in_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Path.Combine(app.DataRoot, "Films");
        await CreateLibraryAsync(app, root);
        HealthCheckOutcome outcome;
        using (FolderWriteDenial.Apply(root))
        {
            outcome = Assert.Single(await RootCheck(app).RunAsync(CancellationToken.None));
        }

        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Contains("cannot write to it", outcome.Message);
        Assert.Contains("refuses writes", outcome.Cause);
        Assert.Contains("\"writable\":false", outcome.DetailsJson);
        Assert.Empty(ProbeLeftovers(root));
    }

    /// <summary>
    /// The thresholds are compared with the free space of the real drive the root is on. They cannot be set above 50 per cent, so which
    /// row is right depends on how full the test machine's drive is; the row asserted is the one the drive's own reading calls for.
    /// </summary>
    [Fact]
    public async Task A_library_root_is_called_low_on_space_exactly_when_the_real_drive_has_no_more_free_than_the_pause_threshold()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Path.Combine(app.DataRoot, "Films");
        await CreateLibraryAsync(app, root);
        (await app.Client.PatchAsJsonAsync("/api/settings", new { libraryFreeSpaceWarnPercent = 50, libraryFreeSpacePausePercent = 50 })).EnsureSuccessStatusCode();
        var drive = new DriveInfo(root);
        var freePercent = (double)drive.AvailableFreeSpace / drive.TotalSize * 100d;

        var outcome = Assert.Single(await RootCheck(app).RunAsync(CancellationToken.None));

        if (freePercent <= 49.9)
        {
            Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
            Assert.Contains("at or below the 50% pause threshold", outcome.Message);
            Assert.Contains("fell to or below the configured pause threshold", outcome.Cause);
        }
        else if (freePercent >= 50.1)
        {
            Assert.Equal(HealthCheckStatuses.Ok, outcome.Status);
            Assert.Contains("reachable and writable", outcome.Message);
        }

        Assert.Empty(ProbeLeftovers(root));
    }

    // ------------------------------------------------------------------ a download that lost its hardlink

    [Fact]
    public async Task A_download_whose_library_copy_was_deleted_is_reported_once_from_the_real_files_and_one_still_linked_is_not()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var inspector = app.Services.GetRequiredService<IHardlinkInspector>();
        var downloads = Directory.CreateDirectory(Path.Combine(app.DataRoot, "downloads")).FullName;
        var library = Directory.CreateDirectory(Path.Combine(app.DataRoot, "movies")).FullName;
        var keptDownload = Path.Combine(downloads, "Sintel.2010.mkv");
        var keptLibrary = Path.Combine(library, "Sintel (2010).mkv");
        var lostDownload = Path.Combine(downloads, "Big.Buck.Bunny.2008.mkv");
        var lostLibrary = Path.Combine(library, "Big Buck Bunny (2008).mkv");
        File.WriteAllBytes(keptDownload, Enumerable.Repeat((byte)7, 2048).ToArray());
        File.WriteAllBytes(lostDownload, Enumerable.Repeat((byte)9, 4096).ToArray());
        RealHardLink.Create(keptDownload, keptLibrary);
        RealHardLink.Create(lostDownload, lostLibrary);
        File.Delete(lostLibrary);

        bool? kept = inspector.StillLinked(keptDownload, keptLibrary);
        bool? lost = inspector.StillLinked(lostDownload, lostLibrary);
        Assert.True(kept);
        Assert.False(lost);

        // The sharing pass writes what it read from the disk into the store; the check reads that and says it once.
        var sharing = app.Services.GetRequiredService<IDownloadSharingRepository>();
        await sharing.ReplaceHoldsAsync(
            [
                new DownloadSharingHold("c1", "qBittorrent", "q1", "Sintel", "2 days left", new FileInfo(keptDownload).Length, false, SharesLibraryCopy: kept == true, LostHardlink: kept == false),
                new DownloadSharingHold("c1", "qBittorrent", "q2", "Big Buck Bunny", "No longer shared with your library.", new FileInfo(lostDownload).Length, false, SharesLibraryCopy: lost == true, LostHardlink: lost == false)
            ],
            driveNote: null,
            CancellationToken.None);

        var outcomes = await new DownloadHardlinkHealthCheck(sharing).RunAsync(CancellationToken.None);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Equal(HealthCheckCategories.DownloadClient, outcome.Category);
        Assert.Contains("Big Buck Bunny", outcome.Message);
        Assert.Contains("no longer shares its file data with your library", outcome.Message);
        Assert.Contains("safe to reclaim", outcome.Fix, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(lostDownload), "the download is not touched by the check");
        Assert.True(File.Exists(keptLibrary));
    }

    [Fact]
    public async Task Two_downloads_that_both_lost_their_hardlink_read_differently()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var sharing = app.Services.GetRequiredService<IDownloadSharingRepository>();
        await sharing.ReplaceHoldsAsync(
            [
                new DownloadSharingHold("c1", "qBittorrent", "q1", "Big Buck Bunny", "Lost.", 1000, false, SharesLibraryCopy: false, LostHardlink: true),
                new DownloadSharingHold("c1", "qBittorrent", "q2", "Sintel", "Lost.", 2000, false, SharesLibraryCopy: false, LostHardlink: true)
            ],
            driveNote: null,
            CancellationToken.None);

        var outcomes = await new DownloadHardlinkHealthCheck(sharing).RunAsync(CancellationToken.None);

        Assert.Equal(2, outcomes.Count);
        Assert.Equal(2, outcomes.Select(outcome => outcome.Id).Distinct().Count());
        Assert.Equal(2, outcomes.Select(outcome => outcome.Name).Distinct().Count());
        Assert.Equal(2, outcomes.Select(outcome => outcome.Message).Distinct().Count());
    }
}
