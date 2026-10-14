using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// The library check and the existing-library import against real folders: what counts as a deleted file and what
/// is only a share that did not mount, what is left alone while an import is replacing a file, and what a run does
/// when its folder disappears under it.
/// </summary>
public sealed class ScanningALibraryOnDiskTests
{
    private static async Task<RealDiskLibrary> StartWorldAsync()
    {
        var app = await ApplicationTestHost.StartAsync();
        Directory.CreateDirectory(Path.Combine(app.DataRoot, "downloads"));
        return await RealDiskLibrary.OpenAsync(app);
    }

    private static string Write(string path, string text = "not really a film")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static async Task<string> AddMovieAsync(RealDiskLibrary world, string title, int year)
    {
        var response = await world.App.Client.PostAsJsonAsync("/api/movies/", new { title, releaseYear = year, monitored = true, libraryId = world.Library.Id, searchForMissingNow = false });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<FilesystemReconciliationReport> ScanAsync(RealDiskLibrary world)
    {
        using var scope = world.App.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IFilesystemReconciliationService>().ScanAsync(CancellationToken.None);
    }

    // ---------------------------------------------------------------- a root that is there but empty

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_library_root_that_exists_but_is_empty_while_files_are_tracked_is_unreachable_not_deleted(bool onlyAVolumeFolder)
    {
        await using var world = await StartWorldAsync();
        var file = Write(Path.Combine(world.Root, "Nosferatu (1922)", "Nosferatu.1922.1080p.BluRay.x264-HD4U.mkv"));
        var id = await AddMovieAsync(world, "Nosferatu", 1922);
        Assert.True((await world.Movies.GetByIdAsync(id, CancellationToken.None))!.HasFile);
        // The share did not mount: the folder is there and nothing is in it (or only the volume's own lost+found).
        Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        if (onlyAVolumeFolder) Directory.CreateDirectory(Path.Combine(world.Root, "lost+found"));

        var report = await ScanAsync(world);

        var issue = Assert.Single(report.Issues);
        Assert.Equal("libraryRootUnreachable", issue.Kind);
        Assert.DoesNotContain(report.Issues, item => item.Kind == "missingTrackedFile");
        Assert.True((await world.Movies.GetByIdAsync(id, CancellationToken.None))!.Monitored, "nothing was unmonitored");
    }

    // ---------------------------------------------------------------- an import is replacing the file

    [Fact]
    public async Task Marking_a_file_missing_is_refused_while_an_import_is_replacing_it_and_writes_nothing()
    {
        await using var world = await StartWorldAsync();
        var kept = Write(Path.Combine(world.Root, "Nosferatu (1922)", "Nosferatu.1922.1080p.BluRay.x264-HD4U.mkv"));
        var other = Write(Path.Combine(world.Root, "Metropolis (1927)", "Metropolis.1927.1080p.BluRay.x264-HD4U.mkv"));
        var id = await AddMovieAsync(world, "Nosferatu", 1922);
        await AddMovieAsync(world, "Metropolis", 1927);
        File.Delete(kept);
        var report = await ScanAsync(world);
        var issue = Assert.Single(report.Issues, item => item.Kind == "missingTrackedFile");
        // Now an import starts replacing it: its backup sits beside the tracked path.
        Write(kept + ".deluno-backup-abc123.tmp", "the old file, set aside by an import");

        using var scope = world.App.Services.CreateScope();
        var repair = await scope.ServiceProvider.GetRequiredService<IFilesystemReconciliationService>()
            .RepairAsync(new FilesystemReconciliationRepairRequest(issue.Id, "mark-missing"), CancellationToken.None);

        Assert.False(repair.Repaired);
        Assert.Contains("An import is replacing this file right now", repair.Message);
        var movie = (await world.Movies.GetByIdAsync(id, CancellationToken.None))!;
        Assert.True(movie.Monitored, "the film is still monitored");
        Assert.True(movie.HasFile, "the catalogue was not written");
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(kept + ".deluno-backup-abc123.tmp"), "the import's backup is untouched");
    }

    // ---------------------------------------------------------------- a file Deluno itself moved to its bin

    [Fact]
    public async Task A_show_file_Deluno_moved_to_its_own_recycle_bin_is_not_reported_as_deleted_outside_Deluno_and_the_show_stays_monitored()
    {
        await using var world = await StartWorldAsync();
        var tvRoot = Path.Combine(world.App.DataRoot, "tv");
        Directory.CreateDirectory(tvRoot);
        var library = await world.App.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name = "tv", mediaType = "tv", purpose = "collection", rootPath = tvRoot, downloadsPath = (string?)null, qualityProfileId = (string?)null,
            importWorkflow = "copy", processorName = (string?)null, processorOutputPath = (string?)null, processorTimeoutMinutes = (int?)null,
            processorFailureMode = (string?)null, autoSearchEnabled = false, missingSearchEnabled = false, upgradeSearchEnabled = false,
            searchIntervalMinutes = (int?)null, retryDelayHours = (int?)null, maxItemsPerRun = (int?)null
        });
        using var libraryDocument = JsonDocument.Parse(await library.Content.ReadAsStringAsync());
        var libraryId = libraryDocument.RootElement.GetProperty("id").GetString()!;
        var episode = Write(Path.Combine(tvRoot, "Nosferatu (1922)", "Season 01", "Nosferatu.S01E01.1080p.WEB-DL.x264-HD4U.mkv"));
        Write(Path.Combine(tvRoot, "Nosferatu (1922)", "Season 01", "Nosferatu.S01E02.1080p.WEB-DL.x264-HD4U.mkv"));
        var added = await world.App.Client.PostAsJsonAsync("/api/series/", new { title = "Nosferatu", startYear = 1922, monitored = true, libraryId, searchForMissingNow = false });
        using var addedDocument = JsonDocument.Parse(await added.Content.ReadAsStringAsync());
        var showId = addedDocument.RootElement.GetProperty("id").GetString()!;
        var shows = world.App.Services.GetRequiredService<ISeriesCatalogRepository>();
        Assert.Equal(2, (await shows.ListEpisodeFileLinksAsync(CancellationToken.None)).Count);
        var tvLibrary = (await world.App.Services.GetRequiredService<Deluno.Libraries.Data.ILibrariesRepository>().ListLibrariesAsync(CancellationToken.None)).Single(item => item.Id == libraryId);
        // Deluno's own bin takes the file (the way the wrong-episode repair does), so the bin knows where it came from.
        // The file the show's own record points at is the one that matters here.
        var recordedPath = (string)(await world.ScalarAsync("series", "SELECT file_path FROM series_wanted_state WHERE file_path IS NOT NULL LIMIT 1;"))!;
        var binned = await world.Bin.StoreReplacementAsync(tvLibrary, recordedPath, recordedPath, CancellationToken.None);
        Assert.NotNull(binned);
        episode = recordedPath;

        var report = await ScanAsync(world);

        Assert.DoesNotContain(report.Issues, item => item.Kind == "missingTrackedFile" && item.Path == episode);
        Assert.Contains(await world.Bin.ListAsync(CancellationToken.None), item => item.OriginalPath == episode);
        Assert.False(File.Exists(episode));
        Assert.True((await shows.GetByIdAsync(showId, CancellationToken.None))!.Monitored, "the show is still monitored");
    }

    // ---------------------------------------------------------------- the import run's folder disappears

    [Fact]
    public async Task A_library_import_run_whose_folder_disappears_between_slices_fails_with_a_plain_message_and_imports_nothing()
    {
        await using var world = await StartWorldAsync();
        Write(Path.Combine(world.Root, "Nosferatu (1922)", "Nosferatu.1922.1080p.BluRay.x264-HD4U.mkv"));
        using var scope = world.App.Services.CreateScope();
        var import = scope.ServiceProvider.GetRequiredService<IExistingLibraryImportService>();
        var started = await import.StartAsync(world.Library.Id, CancellationToken.None);
        Assert.NotNull(started);
        var runId = started!.Run.Id;
        Directory.Delete(world.Root, recursive: true);

        var outcome = await import.RunSliceAsync(runId, CancellationToken.None);

        Assert.Equal("failed", outcome.RunStatus, ignoreCase: true);
        Assert.Contains("The library folder is no longer readable.", outcome.Message);
        Assert.Empty(await world.Movies.ListAsync(CancellationToken.None));
        var progress = await import.GetProgressAsync(world.Library.Id, CancellationToken.None);
        Assert.Equal("failed", progress!.Run.Status, ignoreCase: true);
    }
}
