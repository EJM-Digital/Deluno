using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform;
using Deluno.Platform.Processing;
using Deluno.Worker.HealthChecks;
using Deluno.Worker.HealthChecks.Checks;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// Folders that sit on top of each other (#1325), on a real host and real folders: the save refuses every layout that lets
/// clean-up reach media and accepts every legitimate one; an install that already has an overlap gets a health problem; and
/// every clean-up (Clear, the leftover-downloads remover, an abandoned hand-off's output, a second copy, import recovery)
/// leaves every file where it is while the overlap lasts.
/// </summary>
public sealed class LibraryFolderOverlapOnDiskTests
{
    // ---------------------------------------------------------------- the save refuses

    public static TheoryData<string> RefusedLayouts => new()
    {
        "output-is-the-folder-holding-the-root",
        "output-is-the-root",
        "downloads-is-the-root",
        "downloads-holds-another-librarys-root",
        "downloads-is-another-librarys-root",
        "output-holds-another-librarys-root",
        "root-inside-another-root",
        "root-around-another-root",
        "same-root-as-another-library"
    };

    [Theory]
    [MemberData(nameof(RefusedLayouts))]
    public async Task Creating_a_library_with_an_overlapping_layout_is_refused_with_a_sentence_naming_both_folders(string layout)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var world = await WorldAsync(app);
        var (root, downloads, output) = Overlapping(layout, world);
        EnsureFolders(root, downloads, output);

        using var response = await CreateAsync(app, "Shows", "tv", root, downloads, output);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var message = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetProperty("folders")[0].GetString()!;
        Assert.Contains("Shows", message);
        if (layout is not ("output-is-the-root" or "downloads-is-the-root"))
        {
            Assert.Contains("Films", message);
        }

        Assert.Single(await app.Services.GetRequiredService<ILibrariesRepository>().ListLibrariesAsync(CancellationToken.None));
        Assert.True(File.Exists(world.Film), "the film is where it was");
    }

    [Fact]
    public async Task The_sentence_names_the_two_folders_that_overlap()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var world = await WorldAsync(app);

        using var response = await CreateAsync(app, "Shows", "tv", world.TvRoot, downloads: world.Media, output: null);

        var message = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetProperty("folders")[0].GetString()!;
        Assert.Contains($"The Shows library's downloads folder ({world.Media}) is, or holds, the Films library's folder ({world.MoviesRoot}).", message);
        Assert.True(File.Exists(world.Film), "the film is where it was");
    }

    [Fact]
    public async Task Changing_a_library_to_an_overlapping_layout_is_refused_by_the_details_and_the_workflow_routes_and_nothing_is_saved()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var world = await WorldAsync(app);
        var shows = await CreateOkAsync(app, "Shows", "tv", world.TvRoot, downloads: world.Downloads, output: world.Refined);

        Directory.CreateDirectory(Path.Combine(world.MoviesRoot, "tv"));
        using var holdsARoot = await app.Client.PutAsJsonAsync($"/api/libraries/{shows}", new { name = "Shows", rootPath = world.TvRoot, downloadsPath = world.Media });
        using var insideARoot = await app.Client.PutAsJsonAsync($"/api/libraries/{shows}", new { name = "Shows", rootPath = Path.Combine(world.MoviesRoot, "tv"), downloadsPath = world.Downloads });
        using var outputHoldsARoot = await app.Client.PutAsJsonAsync($"/api/libraries/{shows}/workflow", new { importWorkflow = "refine-before-import", processorName = "Weir", processorOutputPath = world.Media });

        foreach (var response in new[] { holdsARoot, insideARoot, outputHoldsARoot })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("folders", (await response.Content.ReadAsStringAsync()));
        }

        var saved = (await app.Services.GetRequiredService<ILibrariesRepository>().ListLibrariesAsync(CancellationToken.None)).Single(item => item.Id == shows);
        Assert.Equal(world.TvRoot, saved.RootPath);
        Assert.Equal(world.Downloads, saved.DownloadsPath);
        Assert.Equal(world.Refined, saved.ProcessorOutputPath);
    }

    // ---------------------------------------------------------------- the save accepts

    public static TheoryData<string> AllowedLayouts => new()
    {
        "downloads-beside-the-library",
        "output-inside-the-downloads",
        "output-beside-the-downloads",
        "output-is-the-downloads",
        "two-libraries-share-the-downloads",
        "sibling-roots-with-a-shared-name-start",
        "roots-side-by-side-with-own-downloads-and-output",
        "output-inside-the-library-root"
    };

    [Theory]
    [MemberData(nameof(AllowedLayouts))]
    public async Task Every_legitimate_layout_is_still_accepted(string layout)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var world = await WorldAsync(app);
        var (root, downloads, output) = layout switch
        {
            "downloads-beside-the-library" => (world.TvRoot, world.Downloads, (string?)null),
            "output-inside-the-downloads" => (world.TvRoot, world.Downloads, Path.Combine(world.Downloads, "refined")),
            "output-beside-the-downloads" => (world.TvRoot, world.Downloads, world.Refined),
            "output-is-the-downloads" => (world.TvRoot, world.Downloads, world.Downloads),
            "two-libraries-share-the-downloads" => (world.TvRoot, world.FilmsDownloads, (string?)null),
            "sibling-roots-with-a-shared-name-start" => (world.MoviesRoot + "-4k", world.Downloads, (string?)null),
            "roots-side-by-side-with-own-downloads-and-output" => (world.TvRoot, Path.Combine(world.Media, "tv-downloads"), Path.Combine(world.Media, "tv-refined")),
            _ => (world.TvRoot, world.Downloads, Path.Combine(world.TvRoot, "refined"))
        };
        EnsureFolders(root, downloads, output);

        using var response = await CreateAsync(app, "Shows", "tv", root, downloads, output);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(Directory.Exists(root) && File.Exists(world.Film), "the folders and the film are where they were");
    }

    [Fact]
    public async Task A_library_that_already_overlaps_can_still_be_renamed_and_the_overlap_can_be_fixed_in_steps()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var world = await WorldAsync(app);
        await Apply(app, world, "output-is-the-folder-holding-the-root");

        using var rename = await app.Client.PutAsJsonAsync($"/api/libraries/{world.FilmsId}", new { name = "Movies", rootPath = world.MoviesRoot, downloadsPath = world.FilmsDownloads });
        using var fix = await app.Client.PutAsJsonAsync($"/api/libraries/{world.FilmsId}/workflow", new { importWorkflow = "refine-before-import", processorName = "Weir", processorOutputPath = world.Refined });

        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        Assert.Equal(HttpStatusCode.OK, fix.StatusCode);
        Assert.True(File.Exists(world.Film), "the film is where it was");
    }

    // ---------------------------------------------------------------- an install that already overlaps

    public static TheoryData<string> LegacyLayouts => new()
    {
        "output-is-the-folder-holding-the-root",
        "downloads-holds-another-librarys-root",
        "downloads-is-the-root",
        "root-inside-another-root"
    };

    [Theory]
    [MemberData(nameof(LegacyLayouts))]
    public async Task An_overlap_is_a_health_problem_until_it_is_fixed(string layout)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var world = await WorldAsync(app);
        var healthy = await HealthAsync(app);
        Assert.All(healthy, outcome => Assert.Equal(HealthCheckStatuses.Ok, outcome.Status));

        await Apply(app, world, layout);
        var broken = await HealthAsync(app);

        var problem = broken.First(outcome => outcome.Status == HealthCheckStatuses.Problem && outcome.Id.EndsWith(":folder-overlap"));
        Assert.Contains("Deluno will not clean anything up", problem.Message);
        Assert.Contains("Settings, Libraries", problem.Fix);
        Assert.Equal(HealthCheckCategories.Library, problem.Category);

        await Restore(app, world);
        Assert.All(await HealthAsync(app), outcome => Assert.Equal(HealthCheckStatuses.Ok, outcome.Status));
        Assert.True(File.Exists(world.Film), "the check only reads: the film is where it was");
    }

    [Theory]
    [MemberData(nameof(LegacyLayouts))]
    public async Task While_an_overlap_lasts_every_clean_up_leaves_every_file_where_it_is(string layout)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var world = await WorldAsync(app);
        var orphan = await SeedOrphanAsync(app, world);
        var recovery = SeedInterruptedImport(world);
        await Apply(app, world, layout);
        var films = (await app.Services.GetRequiredService<ILibrariesRepository>().ListLibrariesAsync(CancellationToken.None)).ToArray();
        var filmsLibrary = films.Single(item => item.Id == world.FilmsId);
        var everything = films.SelectMany(item => new[] { item.RootPath, item.DownloadsPath, item.ProcessorOutputPath })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct()
            .ToArray();
        var before = Snapshot(world.Media, world.Downloads, world.Refined, world.FilmsDownloads);

        // Clear (processors/output/delete) on every direct child of the output folder, the library's own folder included.
        foreach (var child in Directory.EnumerateFileSystemEntries(filmsLibrary.ProcessorOutputPath!).ToArray())
        {
            using var clear = await app.Client.PostAsJsonAsync("/api/integrations/processors/output/delete", new { libraryId = world.FilmsId, path = child });
            Assert.True(clear.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.BadRequest, $"{child}: {clear.StatusCode}");
        }

        // The leftover-downloads remover, by hand and as the scheduled task.
        using var remove = await app.Client.PostAsJsonAsync("/api/download-clients/orphans/remove", new { path = orphan });
        Assert.Equal(HttpStatusCode.Conflict, remove.StatusCode);
        (await app.Client.PatchAsJsonAsync("/api/settings", new { leftoverDownloadCleanupEnabled = true, leftoverDownloadCleanupAgeHours = 24 })).EnsureSuccessStatusCode();
        using var task = await app.Client.PostAsync("/api/download-clients/leftovers/cleanup", content: null);
        Assert.Equal(0, (await task.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("removed").GetInt32());

        // An abandoned hand-off's output, and a second copy of an import.
        var names = Directory.EnumerateFileSystemEntries(filmsLibrary.ProcessorOutputPath!).Select(Path.GetFileName).Select(name => name!).ToArray();
        Assert.Equal(0, ProcessorHandoffOutputCleanup.CleanupAbandonedHandoffOutput(names, filmsLibrary.ProcessorOutputPath, _ => false, films));
        foreach (var path in Directory.EnumerateFileSystemEntries(world.Media, "*", SearchOption.AllDirectories).Concat(everything).ToArray())
        {
            Assert.False(ProcessorOutputPathPolicy.DeleteSecondCopy(path, filmsLibrary.ProcessorOutputPath, Path.GetFileName(path), films));
        }

        // Import recovery.
        var report = await app.Services.GetRequiredService<IImportRestartRecoveryService>().RecoverAsync(CancellationToken.None);
        Assert.Equal(0, report.Recovered + report.Abandoned);
        Assert.True(File.Exists(recovery.Staging), "the interrupted import's staged file stays");
        Assert.True(File.Exists(recovery.Marker));

        Assert.Equal(before, Snapshot(world.Media, world.Downloads, world.Refined, world.FilmsDownloads));
        Assert.True(File.Exists(world.Film));
        Assert.True(File.Exists(world.Episode));
        Assert.True(Directory.Exists(orphan));
    }

    [Fact]
    public async Task Once_the_layout_is_fixed_clean_up_works_again_and_Clear_puts_the_item_in_the_recycle_bin()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var world = await WorldAsync(app);
        var stray = Path.Combine(world.Refined, "stray.mkv");
        await File.WriteAllTextAsync(stray, "stray");
        await Apply(app, world, "output-is-the-folder-holding-the-root");
        using var blocked = await app.Client.PostAsJsonAsync("/api/integrations/processors/output/delete", new { libraryId = world.FilmsId, path = world.MoviesRoot });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        await Restore(app, world);

        using var cleared = await app.Client.PostAsJsonAsync("/api/integrations/processors/output/delete", new { libraryId = world.FilmsId, path = stray });

        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.False(File.Exists(stray));
        var bin = await app.Services.GetRequiredService<IRecycleBinService>().ListAsync(CancellationToken.None);
        var item = Assert.Single(bin);
        Assert.Equal(stray, item.OriginalPath);
        Assert.Equal("stray", await File.ReadAllTextAsync(item.RecyclePath));
        Assert.True(File.Exists(world.Film));
        Assert.True((await app.Services.GetRequiredService<IRecycleBinService>().RestoreAsync(item.Id, CancellationToken.None)).Success);
        Assert.Equal("stray", await File.ReadAllTextAsync(stray));
    }

    [Fact]
    public async Task Clear_refuses_anything_inside_a_library_root_even_when_the_output_folder_was_chosen_inside_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var world = await WorldAsync(app);
        var inside = Path.Combine(world.MoviesRoot, "refined");
        Directory.CreateDirectory(inside);
        var stray = Path.Combine(inside, "stray.mkv");
        await File.WriteAllTextAsync(stray, "stray");
        using var save = await app.Client.PutAsJsonAsync($"/api/libraries/{world.FilmsId}/workflow", new { importWorkflow = "refine-before-import", processorName = "Weir", processorOutputPath = inside });
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        using var response = await app.Client.PostAsJsonAsync("/api/integrations/processors/output/delete", new { libraryId = world.FilmsId, path = stray });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("inside a library", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
        Assert.True(File.Exists(stray));
        Assert.Empty(await app.Services.GetRequiredService<IRecycleBinService>().ListAsync(CancellationToken.None));
    }

    // ---------------------------------------------------------------- the world

    private sealed record World(
        string Media, string MoviesRoot, string TvRoot, string Downloads, string FilmsDownloads, string Refined,
        string FilmsId, string Film, string Episode);

    private sealed record Interrupted(string Staging, string Marker);

    /// <summary>Films (movies root, its own downloads folder and output folder) with a film on disk, and a TV folder beside it.</summary>
    private static async Task<World> WorldAsync(ApplicationTestHost app)
    {
        var media = Path.Combine(app.DataRoot, "media");
        var movies = Path.Combine(media, "movies");
        var tv = Path.Combine(media, "tv");
        var downloads = Path.Combine(app.DataRoot, "downloads");
        var filmsDownloads = Path.Combine(downloads, "films");
        var refined = Path.Combine(app.DataRoot, "refined");
        var film = Path.Combine(movies, "Film (2020)", "Film.2020.mkv");
        var episode = Path.Combine(tv, "Show (2019)", "Season 01", "Show.S01E01.mkv");
        foreach (var path in new[] { film, episode })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "the only copy of " + Path.GetFileName(path));
        }

        Directory.CreateDirectory(filmsDownloads);
        Directory.CreateDirectory(refined);
        await File.WriteAllTextAsync(Path.Combine(refined, "keep.mkv"), "stray processor output");
        var id = await CreateOkAsync(app, "Films", "movies", movies, filmsDownloads, refined);
        return new World(media, movies, tv, downloads, filmsDownloads, refined, id, film, episode);
    }

    private static void EnsureFolders(params string?[] folders)
    {
        foreach (var folder in folders.Where(folder => !string.IsNullOrWhiteSpace(folder)))
        {
            Directory.CreateDirectory(folder!);
        }
    }

    private static (string Root, string? Downloads, string? Output) Overlapping(string layout, World world) => layout switch
    {
        "output-is-the-folder-holding-the-root" => (world.TvRoot, null, world.Media),
        "output-is-the-root" => (world.TvRoot, null, world.TvRoot),
        "downloads-is-the-root" => (world.TvRoot, world.TvRoot, null),
        "downloads-holds-another-librarys-root" => (world.TvRoot, world.Media, null),
        "downloads-is-another-librarys-root" => (world.TvRoot, world.MoviesRoot, null),
        "output-holds-another-librarys-root" => (world.TvRoot, null, world.Media),
        "root-inside-another-root" => (Path.Combine(world.MoviesRoot, "tv"), null, null),
        "root-around-another-root" => (world.Media, null, null),
        "same-root-as-another-library" => (world.MoviesRoot, null, null),
        _ => throw new ArgumentOutOfRangeException(nameof(layout))
    };

    /// <summary>Puts the Films library into a layout the save would refuse, the way an install saved before the rule has it.</summary>
    private static async Task Apply(ApplicationTestHost app, World world, string layout)
    {
        var repository = app.Services.GetRequiredService<ILibrariesRepository>();
        switch (layout)
        {
            case "output-is-the-folder-holding-the-root":
                await SetOutput(repository, world, world.Media);
                break;
            case "downloads-is-the-root":
                await repository.UpdateLibraryDetailsAsync(world.FilmsId, new UpdateLibraryDetailsRequest("Films", world.MoviesRoot, world.MoviesRoot), CancellationToken.None);
                break;
            case "downloads-holds-another-librarys-root":
            {
                var shows = await CreateOkAsync(app, "Shows", "tv", world.TvRoot, world.Downloads, null);
                await repository.UpdateLibraryDetailsAsync(shows, new UpdateLibraryDetailsRequest("Shows", world.TvRoot, world.Media), CancellationToken.None);
                break;
            }
            case "root-inside-another-root":
            {
                var shows = await CreateOkAsync(app, "Shows", "tv", world.TvRoot, world.Downloads, null);
                var inside = Path.Combine(world.MoviesRoot, "tv");
                Directory.CreateDirectory(inside);
                await repository.UpdateLibraryDetailsAsync(shows, new UpdateLibraryDetailsRequest("Shows", inside, world.Downloads), CancellationToken.None);
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(layout));
        }
    }

    private static async Task Restore(ApplicationTestHost app, World world)
    {
        var repository = app.Services.GetRequiredService<ILibrariesRepository>();
        await repository.UpdateLibraryDetailsAsync(world.FilmsId, new UpdateLibraryDetailsRequest("Films", world.MoviesRoot, world.FilmsDownloads), CancellationToken.None);
        await SetOutput(repository, world, world.Refined);
        foreach (var other in (await repository.ListLibrariesAsync(CancellationToken.None)).Where(item => item.Id != world.FilmsId))
        {
            await repository.UpdateLibraryDetailsAsync(other.Id, new UpdateLibraryDetailsRequest(other.Name, world.TvRoot, world.Downloads), CancellationToken.None);
        }
    }

    private static Task SetOutput(ILibrariesRepository repository, World world, string output)
        => repository.UpdateLibraryWorkflowAsync(
            world.FilmsId,
            new UpdateLibraryWorkflowRequest("refine-before-import", "Weir", output, null, null),
            CancellationToken.None);

    private static Task<HttpResponseMessage> CreateAsync(ApplicationTestHost app, string name, string mediaType, string root, string? downloads, string? output)
        => app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name,
            mediaType,
            purpose = "collection",
            rootPath = root,
            downloadsPath = downloads,
            qualityProfileId = (string?)null,
            importWorkflow = output is null ? "copy" : "refine-before-import",
            processorName = output is null ? null : "Weir",
            processorOutputPath = output,
            processorTimeoutMinutes = (int?)null,
            processorFailureMode = (string?)null,
            autoSearchEnabled = false,
            missingSearchEnabled = false,
            upgradeSearchEnabled = false,
            searchIntervalMinutes = (int?)null,
            retryDelayHours = (int?)null,
            maxItemsPerRun = (int?)null
        });

    private static async Task<string> CreateOkAsync(ApplicationTestHost app, string name, string mediaType, string root, string? downloads, string? output)
    {
        using var response = await CreateAsync(app, name, mediaType, root, downloads, output);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task<IReadOnlyList<HealthCheckOutcome>> HealthAsync(ApplicationTestHost app)
    {
        using var scope = app.Services.CreateScope();
        var check = scope.ServiceProvider.GetServices<IHealthCheck>().OfType<LibraryFolderOverlapHealthCheck>().Single();
        return await check.RunAsync(CancellationToken.None);
    }

    /// <summary>A download Deluno sent and nothing uses, old enough to be cleaned up, in the Films library's downloads folder.</summary>
    private static async Task<string> SeedOrphanAsync(ApplicationTestHost app, World world)
    {
        const string name = "Some.Movie.2020.1080p.WEB";
        var folder = Path.Combine(world.FilmsDownloads, name);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "movie.mkv");
        await File.WriteAllTextAsync(file, "the film");
        var then = DateTime.UtcNow.AddHours(-72);
        File.SetLastWriteTimeUtc(file, then);
        Directory.SetLastWriteTimeUtc(folder, then);
        await app.Services.GetRequiredService<IJobQueueRepository>().RecordDownloadDispatchAsync(
            world.FilmsId, "movies", "movie", Guid.NewGuid().ToString("N"), name, "Indexer", "sabnzbd", "SABnzbd", "queued", null,
            cancellationToken: CancellationToken.None);
        return folder;
    }

    /// <summary>What a restart in the middle of an import leaves inside the library: a staged file and its marker.</summary>
    private static Interrupted SeedInterruptedImport(World world)
    {
        var folder = Path.GetDirectoryName(world.Film)!;
        var destination = Path.Combine(folder, "Sanctuary (2024).mkv");
        var staging = destination + ".deluno-stage-overlaptest.tmp";
        File.WriteAllBytes(staging, [1, 2, 3, 4]);
        File.SetLastWriteTimeUtc(staging, DateTime.UtcNow.AddMinutes(-5));
        var marker = ImportRestartMarkerFile.PathFor(staging);
        Assert.Null(ImportRestartMarkerFile.WriteAtomic(marker, new ImportRestartMarker(Path.Combine(world.FilmsDownloads, "x.mkv"), destination, staging, "movies", "Sanctuary (2024)")));
        return new Interrupted(staging, marker);
    }

    private static string[] Snapshot(params string[] roots)
        => roots.Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            .Distinct()
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
}
