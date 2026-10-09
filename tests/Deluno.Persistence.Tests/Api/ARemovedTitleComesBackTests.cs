using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Media;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform;
using Deluno.Series.Data;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// A title removed with its files can be got back, and adding a title whose
/// files are already in its library folder never searches for them (#1160,
/// #1161).
///
/// <para>Found in the rc.30 live test: Nosferatu was removed with its files,
/// Restore put the folder back but not the title, and adding it again sent the
/// same 6.6 GB release to the download client a second time. Both engines are
/// held to the same contract, which is why every test here runs for a movie
/// and for a show.</para>
/// </summary>
public sealed class ARemovedTitleComesBackTests
{
    [Theory]
    [InlineData(MediaKind.Movie)]
    [InlineData(MediaKind.Series)]
    public async Task Adding_a_title_whose_folder_already_holds_its_file_matches_it_and_does_not_search(MediaKind kind)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await CreateLibraryAsync(app, kind);
        var file = WriteFileInLibrary(app, kind);

        var id = await AddAsync(app, kind, libraryId);

        Assert.True(await HasFileAsync(app, kind, id), "the file already in the folder should have been matched on add");
        Assert.True(File.Exists(file));

        // Nothing was queued to search for it, and nothing to download.
        Assert.Null(await FindJobAsync(app, SearchJobType(kind), id));
        var refreshJob = await FindJobAsync(app, RefreshJobType(kind), id);
        Assert.NotNull(refreshJob);
        using var payload = JsonDocument.Parse(refreshJob!.PayloadJson!);
        Assert.Empty(payload.RootElement.GetProperty("pendingSearches").EnumerateArray());

        // And Activity says why, in plain words.
        var activity = await app.Client.GetStringAsync("/api/activity?take=50");
        Assert.Contains("did not search or download", activity);
    }

    [Theory]
    [InlineData(MediaKind.Movie)]
    [InlineData(MediaKind.Series)]
    public async Task Adding_a_title_with_nothing_in_its_folder_still_searches(MediaKind kind)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await CreateLibraryAsync(app, kind);

        // A search on add is the person's explicit choice (#1145), so the box is ticked here.
        var id = await AddAsync(app, kind, libraryId, searchNow: true);

        Assert.False(await HasFileAsync(app, kind, id));
        var refreshJob = await FindJobAsync(app, RefreshJobType(kind), id);
        using var payload = JsonDocument.Parse(refreshJob!.PayloadJson!);
        Assert.Single(payload.RootElement.GetProperty("pendingSearches").EnumerateArray());
    }

    [Theory]
    [InlineData(MediaKind.Movie)]
    [InlineData(MediaKind.Series)]
    public async Task Removing_a_title_with_its_files_then_restoring_brings_back_the_title_and_the_file(MediaKind kind)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await CreateLibraryAsync(app, kind);
        var file = WriteFileInLibrary(app, kind);
        var id = await AddAsync(app, kind, libraryId);
        Assert.True(await HasFileAsync(app, kind, id));

        var removed = await app.Client.DeleteAsync($"{Route(kind)}{id}?deleteFiles=true");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        // Gone from the catalogue, and in the recycle bin with the title kept beside it.
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync($"{Route(kind)}{id}")).StatusCode);
        Assert.False(File.Exists(file), "the file should have left the library for the recycle bin");
        var bin = await app.Services.GetRequiredService<IRecycleBinService>().ListAsync(CancellationToken.None);
        var item = Assert.Single(bin);
        Assert.NotNull(item.TitleSnapshotId);

        // Activity says where the files went and for how long.
        var afterRemoval = await app.Client.GetStringAsync("/api/activity?take=50");
        Assert.Contains("recycle bin", afterRemoval);
        Assert.Contains("kept for 30 days", afterRemoval);
        Assert.DoesNotContain("selected for deletion", afterRemoval);

        var restore = await app.Client.PostAsync($"/api/recycle-bin/{item.Id}/restore", null);
        var restoreBody = await restore.Content.ReadAsStringAsync();
        Assert.True(restore.IsSuccessStatusCode, restoreBody);
        using (var result = JsonDocument.Parse(restoreBody))
        {
            Assert.True(result.RootElement.GetProperty("titleRestored").GetBoolean(), restoreBody);
            Assert.Equal(id, result.RootElement.GetProperty("titleId").GetString());
        }

        // The same title, with its file, back in the list, and nothing searched.
        Assert.True(File.Exists(file), "the file should be back in the library folder");
        Assert.Equal(HttpStatusCode.OK, (await app.Client.GetAsync($"{Route(kind)}{id}")).StatusCode);
        Assert.True(await HasFileAsync(app, kind, id), "the restored title should still know its file");
        Assert.Null(await FindJobAsync(app, SearchJobType(kind), id));
        Assert.Empty(await app.Services.GetRequiredService<IRecycleBinService>().ListAsync(CancellationToken.None));

        var afterRestore = await app.Client.GetStringAsync("/api/activity?take=50");
        Assert.Contains("back in Deluno with its files, monitoring and history", afterRestore);
    }

    [Theory]
    [InlineData(MediaKind.Movie)]
    [InlineData(MediaKind.Series)]
    public async Task Restoring_brings_back_the_titles_library_its_own_profile_its_tags_and_its_file(MediaKind kind)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await CreateLibraryAsync(app, kind);
        var file = WriteFileInLibrary(app, kind);
        var id = await AddAsync(app, kind, libraryId);
        var mediaState = app.Services.GetRequiredService<IMediaStateRepository>();
        var tagStore = app.Services.GetRequiredService<IMediaTagStore>();

        // The title chooses its own profile, not the library's, and carries a tag.
        var mediaType = kind == MediaKind.Movie ? "movies" : "tv";
        var profile = await app.Client.PostAsJsonAsync("/api/quality-profiles", new
        {
            name = "4K Streaming",
            mediaType,
            cutoffQuality = "WEB 2160p",
            allowedQualities = "WEB 1080p,WEB 2160p",
            customFormatIds = string.Empty,
            upgradeUntilCutoff = true,
            upgradeUnknownItems = false
        });
        Assert.True(profile.IsSuccessStatusCode, await profile.Content.ReadAsStringAsync());
        var profileId = JsonDocument.Parse(await profile.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
        var assign = kind == MediaKind.Movie
            ? await app.Client.PostAsJsonAsync("/api/movies/bulk/quality-profile", new { movieIds = new[] { id }, qualityProfileId = profileId })
            : await app.Client.PostAsJsonAsync("/api/series/bulk/quality-profile", new { seriesIds = new[] { id }, qualityProfileId = profileId });
        Assert.True(assign.IsSuccessStatusCode, await assign.Content.ReadAsStringAsync());
        await tagStore.ReplaceAsync(kind, id, [new MediaTagAssignment("tag-family", "Family")], CancellationToken.None);
        Assert.Equal(profileId, await mediaState.GetTitleQualityProfileIdAsync(kind, id, CancellationToken.None));

        await app.Client.DeleteAsync($"{Route(kind)}{id}?deleteFiles=true");
        Assert.Empty(await tagStore.ListAsync(kind, id, CancellationToken.None));
        var item = Assert.Single(await app.Services.GetRequiredService<IRecycleBinService>().ListAsync(CancellationToken.None));
        var restore = await app.Client.PostAsync($"/api/recycle-bin/{item.Id}/restore", null);
        Assert.True(restore.IsSuccessStatusCode, await restore.Content.ReadAsStringAsync());

        // The same title: its library, its own profile (not the library's), its tag and its file.
        Assert.Equal(HttpStatusCode.OK, (await app.Client.GetAsync($"{Route(kind)}{id}")).StatusCode);
        var wanted = (await mediaState.ListWantedByIdsAsync(kind, [id], CancellationToken.None)).First();
        Assert.Equal(libraryId, wanted.LibraryId);
        Assert.Equal(profileId, await mediaState.GetTitleQualityProfileIdAsync(kind, id, CancellationToken.None));
        var tag = Assert.Single(await tagStore.ListAsync(kind, id, CancellationToken.None));
        Assert.Equal("Family", tag.Name);
        Assert.True(await HasFileAsync(app, kind, id));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Restoring_keeps_a_films_monitoring_choice()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await CreateLibraryAsync(app, MediaKind.Movie);
        WriteFileInLibrary(app, MediaKind.Movie);
        var id = await AddAsync(app, MediaKind.Movie, libraryId, monitored: false);

        await app.Client.DeleteAsync($"/api/movies/{id}?deleteFiles=true");
        var item = Assert.Single(await app.Services.GetRequiredService<IRecycleBinService>().ListAsync(CancellationToken.None));
        Assert.True((await app.Client.PostAsync($"/api/recycle-bin/{item.Id}/restore", null)).IsSuccessStatusCode);

        var movie = await app.Services.GetRequiredService<IMovieCatalogRepository>().GetByIdAsync(id, CancellationToken.None);
        Assert.NotNull(movie);
        Assert.False(movie!.Monitored);
        Assert.True(movie.HasFile);
    }

    [Fact]
    public async Task A_title_added_again_before_it_was_restored_keeps_one_entry_and_matches_the_files_to_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await CreateLibraryAsync(app, MediaKind.Movie);
        var file = WriteFileInLibrary(app, MediaKind.Movie);
        var id = await AddAsync(app, MediaKind.Movie, libraryId);

        await app.Client.DeleteAsync($"/api/movies/{id}?deleteFiles=true");
        var item = Assert.Single(await app.Services.GetRequiredService<IRecycleBinService>().ListAsync(CancellationToken.None));

        // Added again while the files are still in the bin: nothing on disk to find yet.
        var again = await AddAsync(app, MediaKind.Movie, libraryId);
        Assert.False(await HasFileAsync(app, MediaKind.Movie, again));

        var restore = await app.Client.PostAsync($"/api/recycle-bin/{item.Id}/restore", null);
        Assert.True(restore.IsSuccessStatusCode, await restore.Content.ReadAsStringAsync());

        Assert.True(File.Exists(file));
        var movies = await app.Services.GetRequiredService<IMovieCatalogRepository>().ListAsync(CancellationToken.None);
        var only = Assert.Single(movies);
        Assert.True(only.HasFile, "the restored file should be matched to the one entry");
    }

    [Fact]
    public async Task Restoring_an_item_removed_before_titles_were_kept_says_the_title_is_not_back()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await CreateLibraryAsync(app, MediaKind.Movie);
        var file = WriteFileInLibrary(app, MediaKind.Movie);
        var libraries = app.Services.GetRequiredService<Deluno.Libraries.Data.ILibrariesRepository>();
        var library = (await libraries.ListLibrariesAsync(CancellationToken.None)).Single(l => l.Id == libraryId);

        // Moved to the bin the way the old removal did: the folder, with no title record.
        var bin = app.Services.GetRequiredService<IRecycleBinService>();
        await bin.MoveAsync([new TrackedLibraryFile(libraryId, file)], [library], CancellationToken.None);
        var item = Assert.Single(await bin.ListAsync(CancellationToken.None));

        var restore = await app.Client.PostAsync($"/api/recycle-bin/{item.Id}/restore", null);
        var body = await restore.Content.ReadAsStringAsync();
        Assert.True(restore.IsSuccessStatusCode, body);
        using var result = JsonDocument.Parse(body);
        Assert.False(result.RootElement.GetProperty("titleRestored").GetBoolean());
        Assert.Contains("add it again", result.RootElement.GetProperty("message").GetString());

        // Adding it again then finds those files instead of downloading them.
        var id = await AddAsync(app, MediaKind.Movie, libraryId);
        Assert.True(await HasFileAsync(app, MediaKind.Movie, id));
    }

    // ------------------------------------------------------------------ #1189: items binned with no recorded owner

    [Theory]
    [InlineData(MediaKind.Movie)]
    [InlineData(MediaKind.Series)]
    public async Task Restoring_a_file_binned_with_no_owner_finds_its_film_or_episode_from_the_path_and_links_it(MediaKind kind)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await CreateLibraryAsync(app, kind);
        var file = WriteFileInLibrary(app, kind);
        var id = await AddAsync(app, kind, libraryId);
        Assert.True(await HasFileAsync(app, kind, id));
        var library = (await app.Services.GetRequiredService<Deluno.Libraries.Data.ILibrariesRepository>().ListLibrariesAsync(CancellationToken.None)).Single(l => l.Id == libraryId);

        // Binned the way the older writers did: no owner recorded (a bin with no way to look one up).
        var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Deluno.Infrastructure.Storage.StoragePathOptions>>();
        var oldWriter = new RecycleBinService(options, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<RecycleBinService>.Instance);
        var binned = await oldWriter.StoreReplacementAsync(library, file, file, CancellationToken.None);
        Assert.NotNull(binned);
        Assert.Null(binned!.FileOwner);

        // The catalogue lets go of the file, as the wrong-episode repair did.
        if (kind == MediaKind.Movie)
        {
            await app.Services.GetRequiredService<IMovieCatalogRepository>().ClearFileKeepWantedAsync(id, file, CancellationToken.None);
        }
        else
        {
            var link = (await app.Services.GetRequiredService<ISeriesCatalogRepository>().ListEpisodeFileLinksAsync(CancellationToken.None)).Single();
            await app.Services.GetRequiredService<ISeriesCatalogRepository>().ClearMislabelledEpisodeFileAsync(link.EpisodeId, link.FilePath, CancellationToken.None);
        }

        Assert.False(await HasFileAsync(app, kind, id));

        var restore = await app.Client.PostAsync($"/api/recycle-bin/{binned.Id}/restore", null);
        var body = await restore.Content.ReadAsStringAsync();
        Assert.True(restore.IsSuccessStatusCode, body);
        Assert.True(File.Exists(file));
        Assert.True(await HasFileAsync(app, kind, id), "the restored file should have been linked to its film or episode: " + body);
        using var result = JsonDocument.Parse(body);
        var message = result.RootElement.GetProperty("message").GetString();
        Assert.Contains(kind == MediaKind.Movie ? "Nosferatu has its file again." : "Nosferatu S01E01 has its file again.", message);
    }

    [Theory]
    [InlineData(MediaKind.Movie)]
    [InlineData(MediaKind.Series)]
    public async Task Every_file_the_bin_takes_records_the_film_or_episode_it_belonged_to(MediaKind kind)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await CreateLibraryAsync(app, kind);
        var file = WriteFileInLibrary(app, kind);
        var id = await AddAsync(app, kind, libraryId);
        var library = (await app.Services.GetRequiredService<Deluno.Libraries.Data.ILibrariesRepository>().ListLibrariesAsync(CancellationToken.None)).Single(l => l.Id == libraryId);

        // A replacement, which has no owner of its own to give.
        var bin = app.Services.GetRequiredService<IRecycleBinService>();
        var item = await bin.StoreReplacementAsync(library, file, file, CancellationToken.None);

        Assert.NotNull(item?.FileOwner);
        Assert.Equal(id, item!.FileOwner!.TitleId);
        Assert.Equal(kind == MediaKind.Movie, item.FileOwner.EpisodeId is null);
    }

    [Fact]
    public async Task Restoring_a_file_whose_title_cannot_be_found_says_so_plainly_and_points_at_import_existing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await CreateLibraryAsync(app, MediaKind.Movie);
        var folder = Path.Combine(app.DataRoot, "movies", "Unknown Film (1999)");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "Unknown.Film.1999.1080p.BluRay.x264-HD4U.mkv");
        File.WriteAllText(file, "not really a film");
        var library = (await app.Services.GetRequiredService<Deluno.Libraries.Data.ILibrariesRepository>().ListLibrariesAsync(CancellationToken.None)).Single(l => l.Id == libraryId);
        var bin = app.Services.GetRequiredService<IRecycleBinService>();
        var item = await bin.StoreReplacementAsync(library, file, file, CancellationToken.None);
        Assert.NotNull(item);
        Assert.Null(item!.FileOwner);

        var restore = await app.Client.PostAsync($"/api/recycle-bin/{item.Id}/restore", null);
        var body = await restore.Content.ReadAsStringAsync();
        Assert.True(restore.IsSuccessStatusCode, body);
        using var result = JsonDocument.Parse(body);
        var message = result.RootElement.GetProperty("message").GetString();
        Assert.Contains("could not tell which film it belongs to", message);
        Assert.Contains("Import Existing", message);
        Assert.True(File.Exists(file));
    }

    // ------------------------------------------------------------------ helpers

    private static string Route(MediaKind kind) => kind == MediaKind.Movie ? "/api/movies/" : "/api/series/";

    private static string SearchJobType(MediaKind kind) => LibrarySearchJobTypes.For(kind == MediaKind.Movie ? "movies" : "tv");

    private static string RefreshJobType(MediaKind kind) => kind == MediaKind.Movie ? "movies.catalog.refresh" : "series.catalog.refresh";

    /// <summary>The film's file in its title folder, or the show's first episode.</summary>
    private static string WriteFileInLibrary(ApplicationTestHost app, MediaKind kind)
    {
        var root = Path.Combine(app.DataRoot, kind == MediaKind.Movie ? "movies" : "tv");
        var folder = kind == MediaKind.Movie
            ? Path.Combine(root, "Nosferatu (1922)")
            : Path.Combine(root, "Nosferatu (1922)", "Season 01");
        Directory.CreateDirectory(folder);
        var name = kind == MediaKind.Movie
            ? "Nosferatu.1922.1080p.BluRay.x264-HD4U.mkv"
            : "Nosferatu.S01E01.1080p.WEB-DL.x264-HD4U.mkv";
        var file = Path.Combine(folder, name);
        File.WriteAllText(file, "not really a film");
        return file;
    }

    private static async Task<string> AddAsync(ApplicationTestHost app, MediaKind kind, string libraryId, bool monitored = true, bool searchNow = false)
    {
        object body = kind == MediaKind.Movie
            ? new { title = "Nosferatu", releaseYear = 1922, monitored, libraryId, searchForMissingNow = searchNow }
            : new { title = "Nosferatu", startYear = 1922, monitored, libraryId, searchForMissingNow = searchNow };
        var response = await app.Client.PostAsJsonAsync(Route(kind), body);
        Assert.True(response.IsSuccessStatusCode, $"POST {Route(kind)} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<bool> HasFileAsync(ApplicationTestHost app, MediaKind kind, string id)
    {
        if (kind == MediaKind.Movie)
        {
            return (await app.Services.GetRequiredService<IMovieCatalogRepository>().GetByIdAsync(id, CancellationToken.None))?.HasFile ?? false;
        }

        return (await app.Services.GetRequiredService<ISeriesCatalogRepository>().GetByIdAsync(id, CancellationToken.None))?.HasFile ?? false;
    }

    private static async Task<JobQueueItem?> FindJobAsync(ApplicationTestHost app, string jobType, string relatedEntityId)
    {
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var all = await jobs.ListAsync(500, CancellationToken.None);
        return all.FirstOrDefault(job =>
            string.Equals(job.JobType, jobType, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(job.RelatedEntityId, relatedEntityId, StringComparison.Ordinal));
    }

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
            searchIntervalMinutes = (int?)null,
            retryDelayHours = (int?)null,
            maxItemsPerRun = (int?)null
        });
        Assert.True(response.IsSuccessStatusCode, $"POST /api/libraries/ returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }
}
