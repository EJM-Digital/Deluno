using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// Deleting a file or removing a title, through the real routes on a real temp folder with the real recycle bin and
/// real SQLite catalogues. Media only ever leaves a library through the bin, only the named file or the title's own
/// folder moves, and a failure leaves the files where they were or says where they went.
/// </summary>
public sealed class DeletingFilesAndTitlesFromDiskTests
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

    /// <summary>Adds a film by name; the film file already in its folder is matched on add.</summary>
    private static async Task<string> AddMovieAsync(RealDiskLibrary world, string title, int year)
    {
        var response = await world.App.Client.PostAsJsonAsync("/api/movies/", new { title, releaseYear = year, monitored = true, libraryId = world.Library.Id, searchForMissingNow = false });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<(string LibraryId, string Root)> CreateTvLibraryAsync(ApplicationTestHost app)
    {
        var root = Path.Combine(app.DataRoot, "tv");
        Directory.CreateDirectory(root);
        var response = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name = "tv", mediaType = "tv", purpose = "collection", rootPath = root, downloadsPath = (string?)null, qualityProfileId = (string?)null,
            importWorkflow = "copy", processorName = (string?)null, processorOutputPath = (string?)null, processorTimeoutMinutes = (int?)null,
            processorFailureMode = (string?)null, autoSearchEnabled = false, missingSearchEnabled = false, upgradeSearchEnabled = false,
            searchIntervalMinutes = (int?)null, retryDelayHours = (int?)null, maxItemsPerRun = (int?)null
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (document.RootElement.GetProperty("id").GetString()!, root);
    }

    private static async Task<string> AddShowAsync(ApplicationTestHost app, string libraryId)
    {
        var response = await app.Client.PostAsJsonAsync("/api/series/", new { title = "Nosferatu", startYear = 1922, monitored = true, libraryId, searchForMissingNow = false });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }

    // ---------------------------------------------------------------- one file of a film

    [Fact]
    public async Task Deleting_a_films_file_when_the_recycle_bin_cannot_take_it_answers_500_and_leaves_the_file_and_the_catalogue_alone()
    {
        await using var world = await StartWorldAsync();
        var file = Write(Path.Combine(world.Root, "Nosferatu (1922)", "Nosferatu.1922.1080p.BluRay.x264-HD4U.mkv"));
        var id = await AddMovieAsync(world, "Nosferatu", 1922);
        Assert.True((await world.Movies.GetByIdAsync(id, CancellationToken.None))!.HasFile);
        // A plain file sits where the holding folder must be made, so the bin cannot take anything.
        File.WriteAllText(world.Root + ".deluno-recycle-bin", "not a folder");

        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/movies/{id}/files") { Content = JsonContent.Create(new { path = file }) };
        var response = await world.App.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("could not be moved to the recycle bin, so it was left where it is", await response.Content.ReadAsStringAsync());
        Assert.True(File.Exists(file));
        var movie = (await world.Movies.GetByIdAsync(id, CancellationToken.None))!;
        Assert.True(movie.HasFile, "the catalogue still holds the file");
        Assert.Equal(file, movie.FilePath);
        Assert.Empty(await world.Bin.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Deleting_a_films_file_moves_only_that_file_to_the_bin_and_keeps_the_film_wanted()
    {
        await using var world = await StartWorldAsync();
        var file = Write(Path.Combine(world.Root, "Nosferatu (1922)", "Nosferatu.1922.1080p.BluRay.x264-HD4U.mkv"));
        var extra = Write(Path.Combine(world.Root, "Nosferatu (1922)", "Nosferatu.1922.1080p.BluRay.x264-HD4U.en.srt"), "subtitle");
        var id = await AddMovieAsync(world, "Nosferatu", 1922);

        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/movies/{id}/files") { Content = JsonContent.Create(new { path = file }) };
        var response = await world.App.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(File.Exists(file));
        Assert.True(File.Exists(extra), "the file beside it stays");
        var item = Assert.Single(await world.Bin.ListAsync(CancellationToken.None));
        Assert.Equal(file, item.OriginalPath);
        Assert.False((await world.Movies.GetByIdAsync(id, CancellationToken.None))!.HasFile);
    }

    // ---------------------------------------------------------------- a subtitle's language

    [Theory]
    [InlineData(false, "Nosferatu (1922).en.srt")]
    [InlineData(true, "Nosferatu (1922).en.sdh.srt")]
    public async Task Marking_a_subtitles_language_renames_the_file_on_disk_to_its_language_name(bool hearingImpaired, string expectedName)
    {
        await using var world = await StartWorldAsync();
        Write(Path.Combine(world.Root, "Nosferatu (1922)", "Nosferatu (1922).mkv"));
        var subtitle = Write(Path.Combine(world.Root, "Nosferatu (1922)", "Nosferatu (1922).srt"), "subtitle");
        var id = await AddMovieAsync(world, "Nosferatu", 1922);

        var response = await world.App.Client.PutAsJsonAsync($"/api/movies/{id}/files/language", new { path = subtitle, language = "English", hearingImpaired });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var expected = Path.Combine(Path.GetDirectoryName(subtitle)!, expectedName);
        Assert.True(File.Exists(expected));
        Assert.False(File.Exists(subtitle));
        Assert.Equal("subtitle", File.ReadAllText(expected));
    }

    [Fact]
    public async Task Marking_a_subtitles_language_refuses_a_path_outside_the_title_folder_a_blank_language_and_a_name_already_taken()
    {
        await using var world = await StartWorldAsync();
        Write(Path.Combine(world.Root, "Nosferatu (1922)", "Nosferatu (1922).mkv"));
        var subtitle = Write(Path.Combine(world.Root, "Nosferatu (1922)", "Nosferatu (1922).srt"), "mine");
        var taken = Write(Path.Combine(world.Root, "Nosferatu (1922)", "Nosferatu (1922).en.srt"), "already here");
        var stranger = Write(Path.Combine(world.App.DataRoot, "elsewhere", "Other.srt"), "not in the folder");
        var id = await AddMovieAsync(world, "Nosferatu", 1922);

        var outside = await world.App.Client.PutAsJsonAsync($"/api/movies/{id}/files/language", new { path = stranger, language = "English" });
        var blank = await world.App.Client.PutAsJsonAsync($"/api/movies/{id}/files/language", new { path = subtitle, language = "  " });
        var clash = await world.App.Client.PutAsJsonAsync($"/api/movies/{id}/files/language", new { path = subtitle, language = "English" });

        Assert.Equal(HttpStatusCode.BadRequest, outside.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Contains("Choose a language", await blank.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        Assert.Contains("already exists", await clash.Content.ReadAsStringAsync());
        Assert.Equal("mine", File.ReadAllText(subtitle));
        Assert.Equal("already here", File.ReadAllText(taken));
        Assert.Equal("not in the folder", File.ReadAllText(stranger));
    }

    [Fact]
    public async Task Marking_a_subtitles_language_when_the_rename_fails_answers_500_and_leaves_the_file_where_it_was()
    {
        await using var world = await StartWorldAsync();
        Write(Path.Combine(world.Root, "Nosferatu (1922)", "Nosferatu (1922).mkv"));
        var subtitle = Write(Path.Combine(world.Root, "Nosferatu (1922)", "Nosferatu (1922).srt"), "mine");
        var id = await AddMovieAsync(world, "Nosferatu", 1922);
        using var block = DeleteBlock.TryApply(subtitle);
        if (block is null)
        {
            return; // this account can rename anything (for example root in a container), so a rename cannot be made to fail
        }

        var response = await world.App.Client.PutAsJsonAsync($"/api/movies/{id}/files/language", new { path = subtitle, language = "English" });
        block.Dispose();

        Assert.True(response.StatusCode is HttpStatusCode.InternalServerError, "status was " + response.StatusCode);
        Assert.Contains("could not be renamed", await response.Content.ReadAsStringAsync());
        Assert.Equal("mine", File.ReadAllText(subtitle));
        Assert.False(File.Exists(Path.ChangeExtension(subtitle, ".en.srt")));
    }

    // ---------------------------------------------------------------- an episode's file

    [Fact]
    public async Task Deleting_an_episode_file_whose_recorded_path_is_inside_no_library_is_refused_and_the_file_is_untouched()
    {
        await using var world = await StartWorldAsync();
        var (libraryId, tvRoot) = await CreateTvLibraryAsync(world.App);
        Write(Path.Combine(tvRoot, "Nosferatu (1922)", "Season 01", "Nosferatu.S01E01.1080p.WEB-DL.x264-HD4U.mkv"));
        var showId = await AddShowAsync(world.App, libraryId);
        var strangerFolder = Path.Combine(world.App.DataRoot, "not-a-library");
        var stranger = Write(Path.Combine(strangerFolder, "Nosferatu.S01E01.mkv"), "someone else's file");
        await world.SqlAsync("series", $"UPDATE episode_entries SET file_path = '{stranger.Replace("'", "''")}' WHERE has_file = 1;");
        var episodeId = (await world.App.Services.GetRequiredService<Deluno.Series.Data.ISeriesCatalogRepository>().ListEpisodeFileLinksAsync(CancellationToken.None)).Single().EpisodeId;

        var response = await world.App.Client.DeleteAsync($"/api/series/episodes/{episodeId}/file");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("not inside one of your libraries", await response.Content.ReadAsStringAsync());
        Assert.Equal("someone else's file", File.ReadAllText(stranger));
        Assert.Empty(await world.Bin.ListAsync(CancellationToken.None));
        Assert.NotNull(showId);
    }

    [Fact]
    public async Task Deleting_an_episode_file_that_is_already_gone_from_disk_still_clears_the_record()
    {
        await using var world = await StartWorldAsync();
        var (libraryId, tvRoot) = await CreateTvLibraryAsync(world.App);
        var file = Write(Path.Combine(tvRoot, "Nosferatu (1922)", "Season 01", "Nosferatu.S01E01.1080p.WEB-DL.x264-HD4U.mkv"));
        var showId = await AddShowAsync(world.App, libraryId);
        var repository = world.App.Services.GetRequiredService<Deluno.Series.Data.ISeriesCatalogRepository>();
        var episodeId = (await repository.ListEpisodeFileLinksAsync(CancellationToken.None)).Single().EpisodeId;
        File.Delete(file);

        var response = await world.App.Client.DeleteAsync($"/api/series/episodes/{episodeId}/file");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await repository.ListEpisodeFileLinksAsync(CancellationToken.None));
        Assert.Empty(await world.Bin.ListAsync(CancellationToken.None));
        Assert.NotNull(showId);
    }

    // ---------------------------------------------------------------- removing a whole title

    [Fact]
    public async Task Removing_a_show_without_deleteFiles_leaves_every_episode_file_on_disk_and_the_bin_empty()
    {
        await using var world = await StartWorldAsync();
        var (libraryId, tvRoot) = await CreateTvLibraryAsync(world.App);
        var episode = Write(Path.Combine(tvRoot, "Nosferatu (1922)", "Season 01", "Nosferatu.S01E01.1080p.WEB-DL.x264-HD4U.mkv"));
        var showId = await AddShowAsync(world.App, libraryId);

        var removed = await world.App.Client.DeleteAsync($"/api/series/{showId}");

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await world.App.Client.GetAsync($"/api/series/{showId}")).StatusCode);
        Assert.True(File.Exists(episode), "without deleteFiles the files stay on disk");
        Assert.Empty(await world.Bin.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Removing_a_film_with_its_files_when_the_catalogue_row_cannot_be_deleted_fails_but_the_files_are_in_the_bin_and_restorable()
    {
        await using var world = await StartWorldAsync();
        var file = Write(Path.Combine(world.Root, "Nosferatu (1922)", "Nosferatu.1922.1080p.BluRay.x264-HD4U.mkv"));
        var id = await AddMovieAsync(world, "Nosferatu", 1922);
        // The delete is silently skipped by the database, so the row stays.
        await world.SqlAsync("movies", "CREATE TRIGGER keep_the_film BEFORE DELETE ON movie_entries BEGIN SELECT RAISE(IGNORE); END;");

        // The route has no handler of its own for this, so the host's error page answers; in a test host the exception shows.
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => world.App.Client.DeleteAsync($"/api/movies/{id}?deleteFiles=true"));

        Assert.Contains("Movie was not removed from Deluno", failure.Message);
        Assert.NotNull(await world.Movies.GetByIdAsync(id, CancellationToken.None));
        Assert.False(File.Exists(file));
        var item = Assert.Single(await world.Bin.ListAsync(CancellationToken.None));
        var restored = await world.Bin.RestoreAsync(item.Id, CancellationToken.None);
        Assert.True(restored.Success, restored.Message);
        Assert.True(File.Exists(file));
    }

    // DEFECT: TitleRemovalService.MoveAsync moves the first folder under the library root. In a library laid out in group
    // folders (/movies/A/Dune (2021)/Dune.mkv) that is the group folder, so every other title in it goes to the bin too.
    // Observed: Arrival's file leaves the library with Dune's and Arrival is left with a missing file.
    [Fact]
    public async Task Removing_a_film_with_its_files_takes_only_its_own_folder_when_another_title_lives_in_a_sibling_folder_of_the_same_group()
    {
        await using var world = await StartWorldAsync();
        var dune = Write(Path.Combine(world.Root, "A", "Dune (2021)", "Dune.2021.1080p.BluRay.x264-GRP.mkv"));
        var other = Write(Path.Combine(world.Root, "A", "Arrival (2016)", "Arrival.2016.1080p.BluRay.x264-GRP.mkv"));
        // Files in a group folder are not matched on add, so they are recorded the way a library import records them.
        var duneMovie = await world.AddMovieAsync("Dune", 2021);
        var arrivalMovie = await world.AddMovieAsync("Arrival", 2016);
        await world.Movies.ImportExistingAsync(world.Library.Id, "Dune", 2021, "covered", "test", "BluRay-1080p", null, true, dune, 17, CancellationToken.None, movieId: duneMovie.Id);
        await world.Movies.ImportExistingAsync(world.Library.Id, "Arrival", 2016, "covered", "test", "BluRay-1080p", null, true, other, 17, CancellationToken.None, movieId: arrivalMovie.Id);
        var duneId = duneMovie.Id;
        var arrivalId = arrivalMovie.Id;
        Assert.True((await world.Movies.GetByIdAsync(duneId, CancellationToken.None))!.HasFile);
        Assert.True((await world.Movies.GetByIdAsync(arrivalId, CancellationToken.None))!.HasFile);

        var removed = await world.App.Client.DeleteAsync($"/api/movies/{duneId}?deleteFiles=true");

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.False(File.Exists(dune), "Dune's file left the library");
        Assert.True(File.Exists(other), "Arrival, in a different folder of the same group, stays where it is");
        Assert.True((await world.Movies.GetByIdAsync(arrivalId, CancellationToken.None))!.HasFile);
    }

    // DEFECT: the same unit rule moves the whole title folder, so a second title whose tracked file sits in that folder loses its file.
    [Fact]
    public async Task Removing_a_film_with_its_files_leaves_another_titles_file_that_sits_in_the_same_folder()
    {
        await using var world = await StartWorldAsync();
        var first = Write(Path.Combine(world.Root, "Shared (2000)", "Shared.2000.1080p.BluRay.x264-GRP.mkv"));
        var second = Write(Path.Combine(world.Root, "Shared (2000)", "Shared.2000.Extended.2160p.BluRay.x265-GRP.mkv"));
        var firstId = await AddMovieAsync(world, "Shared", 2000);
        // A second catalogue title whose tracked file is in the first one's folder.
        var secondMovie = await world.AddMovieAsync("Shared Extended", 2000);
        await world.Movies.ImportExistingAsync(world.Library.Id, "Shared Extended", 2000, "covered", "test", "BluRay-2160p", null, true, second, 17, CancellationToken.None, movieId: secondMovie.Id);

        var removed = await world.App.Client.DeleteAsync($"/api/movies/{firstId}?deleteFiles=true");

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.False(File.Exists(first), "the removed title's file left");
        Assert.True(File.Exists(second), "the other title's file in the same folder stays");
        Assert.True((await world.Movies.GetByIdAsync(secondMovie.Id, CancellationToken.None))!.HasFile);
    }
}
