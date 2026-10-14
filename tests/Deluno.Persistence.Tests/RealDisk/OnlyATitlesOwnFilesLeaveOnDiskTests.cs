using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// Refs #1317. Whatever takes a title's files out of a library (removing a film, removing a show, a retention rule on
/// its day, "download this again" from another tool) moves only that title's tracked files and the sidecars beside them,
/// and a folder only when nothing else is left in it. Each caller is proven on a real temp library with a folder shared
/// with another title and a group folder holding other titles.
/// </summary>
public sealed class OnlyATitlesOwnFilesLeaveOnDiskTests
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

    private static async Task<string> AddMovieByNameAsync(RealDiskLibrary world, string title, int year)
    {
        var response = await world.App.Client.PostAsJsonAsync("/api/movies/", new { title, releaseYear = year, monitored = true, libraryId = world.Library.Id, searchForMissingNow = false });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>A film whose file sits in a group folder (not matched on add), recorded the way a library import records it.</summary>
    private static async Task<string> TrackAsync(RealDiskLibrary world, string title, int year, string path)
    {
        var movie = await world.AddMovieAsync(title, year);
        await world.Movies.ImportExistingAsync(world.Library.Id, title, year, "covered", "test", "BluRay-1080p", null, true, path, 17, CancellationToken.None, movieId: movie.Id);
        Assert.True((await world.Movies.GetByIdAsync(movie.Id, CancellationToken.None))!.HasFile);
        return movie.Id;
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

    // ---------------------------------------------------------------- film removal

    [Fact]
    public async Task Removing_a_film_alone_in_its_folder_takes_the_folder_with_its_subtitle_artwork_and_notes_and_restore_puts_it_all_back()
    {
        await using var world = await StartWorldAsync();
        var folder = Path.Combine(world.Root, "Nosferatu (1922)");
        var film = Write(Path.Combine(folder, "Nosferatu.1922.1080p.BluRay.x264-HD4U.mkv"));
        var subtitle = Write(Path.Combine(folder, "Nosferatu.1922.1080p.BluRay.x264-HD4U.en.srt"), "subtitle");
        var poster = Write(Path.Combine(folder, "poster.jpg"), "poster");
        var notes = Write(Path.Combine(folder, "movie.nfo"), "notes");
        var id = await AddMovieByNameAsync(world, "Nosferatu", 1922);

        var removed = await world.App.Client.DeleteAsync($"/api/movies/{id}?deleteFiles=true");

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.False(Directory.Exists(folder), "nothing of the title is left behind");
        var item = Assert.Single(await world.Bin.ListAsync(CancellationToken.None));
        Assert.True(item.IsDirectory);
        var restored = await world.Bin.RestoreAsync(item.Id, CancellationToken.None);
        Assert.True(restored.Success, restored.Message);
        Assert.All(new[] { film, subtitle, poster, notes }, path => Assert.True(File.Exists(path), path));
    }

    [Fact]
    public async Task Removing_a_film_that_shares_its_folder_takes_its_file_and_sidecars_only_and_leaves_the_other_titles_subtitle_and_video()
    {
        await using var world = await StartWorldAsync();
        var folder = Path.Combine(world.Root, "Shared (2000)");
        var first = Write(Path.Combine(folder, "Shared.2000.1080p.BluRay.x264-GRP.mkv"));
        var firstSubtitle = Write(Path.Combine(folder, "Shared.2000.1080p.BluRay.x264-GRP.en.srt"), "mine");
        var other = Write(Path.Combine(folder, "Shared.2000.Part.Two.2160p.BluRay.x265-GRP.mkv"));
        var otherSubtitle = Write(Path.Combine(folder, "Shared.2000.Part.Two.2160p.BluRay.x265-GRP.en.srt"), "theirs");
        var unrelated = Write(Path.Combine(folder, "notes-from-the-set.pdf"), "pdf");
        var id = await AddMovieByNameAsync(world, "Shared", 2000);
        Assert.Equal(first, (await world.Movies.GetByIdAsync(id, CancellationToken.None))!.FilePath);

        var removed = await world.App.Client.DeleteAsync($"/api/movies/{id}?deleteFiles=true");

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(firstSubtitle), "its subtitle goes with it");
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(otherSubtitle), "the other title's subtitle stays");
        Assert.True(File.Exists(unrelated));
        var items = await world.Bin.ListAsync(CancellationToken.None);
        Assert.All(items, item => Assert.False(item.IsDirectory, "the shared folder is not moved"));
        Assert.Equal(new[] { first, firstSubtitle }.Order(), items.Select(item => item.OriginalPath).Order());
    }

    [Fact]
    public async Task Removing_the_only_film_in_a_group_folder_takes_that_folder_and_one_beside_other_titles_takes_only_its_own_folder()
    {
        await using var world = await StartWorldAsync();
        var dune = Write(Path.Combine(world.Root, "D", "Dune (2021)", "Dune.2021.1080p.BluRay.x264-GRP.mkv"));
        var arrival = Write(Path.Combine(world.Root, "A", "Arrival (2016)", "Arrival.2016.1080p.BluRay.x264-GRP.mkv"));
        var alien = Write(Path.Combine(world.Root, "A", "Alien (1979)", "Alien.1979.1080p.BluRay.x264-GRP.mkv"));
        var duneId = await TrackAsync(world, "Dune", 2021, dune);
        var arrivalId = await TrackAsync(world, "Arrival", 2016, arrival);
        await TrackAsync(world, "Alien", 1979, alien);

        Assert.Equal(HttpStatusCode.NoContent, (await world.App.Client.DeleteAsync($"/api/movies/{arrivalId}?deleteFiles=true")).StatusCode);
        Assert.False(Directory.Exists(Path.Combine(world.Root, "A", "Arrival (2016)")), "its own folder moved");
        Assert.True(File.Exists(alien), "the other title in the group stays");

        Assert.Equal(HttpStatusCode.NoContent, (await world.App.Client.DeleteAsync($"/api/movies/{duneId}?deleteFiles=true")).StatusCode);
        Assert.False(File.Exists(dune));
        Assert.False(Directory.Exists(Path.Combine(world.Root, "D")), "a group folder left empty is tidied away");
        Assert.True(Directory.Exists(Path.Combine(world.Root, "A")));
        Assert.True(File.Exists(alien));
    }

    // ---------------------------------------------------------------- series removal

    [Fact]
    public async Task Removing_a_show_takes_its_episodes_subtitles_and_artwork_with_its_folder_and_leaves_another_show_beside_it()
    {
        await using var world = await StartWorldAsync();
        var (libraryId, tvRoot) = await CreateTvLibraryAsync(world.App);
        var season = Path.Combine(tvRoot, "Nosferatu (1922)", "Season 01");
        var episode = Write(Path.Combine(season, "Nosferatu.S01E01.1080p.WEB-DL.x264-HD4U.mkv"));
        var subtitle = Write(Path.Combine(season, "Nosferatu.S01E01.1080p.WEB-DL.x264-HD4U.en.srt"), "subtitle");
        var poster = Write(Path.Combine(tvRoot, "Nosferatu (1922)", "poster.jpg"), "poster");
        var neighbour = Write(Path.Combine(tvRoot, "Dracula (1931)", "Season 01", "Dracula.S01E01.mkv"), "someone else's show");
        var showId = await AddShowAsync(world.App, libraryId);

        var removed = await world.App.Client.DeleteAsync($"/api/series/{showId}?deleteFiles=true");

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.False(File.Exists(episode));
        Assert.False(File.Exists(subtitle));
        Assert.False(File.Exists(poster));
        Assert.False(Directory.Exists(Path.Combine(tvRoot, "Nosferatu (1922)")), "nothing of the show is left behind");
        Assert.Equal("someone else's show", File.ReadAllText(neighbour));
        var item = Assert.Single(await world.Bin.ListAsync(CancellationToken.None));
        Assert.True(item.IsDirectory);
    }

    [Fact]
    public async Task Removing_a_show_in_a_group_folder_takes_only_its_own_folder_and_leaves_the_other_shows_in_the_group()
    {
        await using var world = await StartWorldAsync();
        var (libraryId, tvRoot) = await CreateTvLibraryAsync(world.App);
        Write(Path.Combine(tvRoot, "Nosferatu (1922)", "Season 01", "Nosferatu.S01E01.1080p.WEB-DL.x264-HD4U.mkv"));
        var showId = await AddShowAsync(world.App, libraryId);
        // The library is laid out in group folders: the show is filed under N/, beside another show.
        var group = Path.Combine(tvRoot, "N");
        Directory.CreateDirectory(group);
        Directory.Move(Path.Combine(tvRoot, "Nosferatu (1922)"), Path.Combine(group, "Nosferatu (1922)"));
        var episode = Path.Combine(group, "Nosferatu (1922)", "Season 01", "Nosferatu.S01E01.1080p.WEB-DL.x264-HD4U.mkv");
        await world.SqlAsync("series", $"UPDATE episode_entries SET file_path = '{episode.Replace("'", "''")}' WHERE has_file = 1;");
        await world.SqlAsync("series", $"UPDATE series_wanted_state SET file_path = '{episode.Replace("'", "''")}' WHERE file_path IS NOT NULL AND file_path <> '';");
        var neighbour = Write(Path.Combine(group, "Nosferatu The Vampyre (1979)", "Season 01", "Nosferatu.The.Vampyre.S01E01.mkv"));

        var removed = await world.App.Client.DeleteAsync($"/api/series/{showId}?deleteFiles=true");

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.False(File.Exists(episode));
        Assert.True(File.Exists(neighbour), "the other show in the group stays");
        Assert.True(Directory.Exists(group));
        Assert.False(Directory.Exists(Path.Combine(group, "Nosferatu (1922)")));
    }

    // ---------------------------------------------------------------- external re-download

    [Fact]
    public async Task Downloading_a_film_again_moves_only_its_file_and_subtitle_when_another_video_shares_the_folder()
    {
        await using var world = await StartWorldAsync();
        var folder = Path.Combine(world.Root, "Shared (2000)");
        var file = Write(Path.Combine(folder, "Shared.2000.1080p.BluRay.x264-GRP.mkv"));
        var subtitle = Write(Path.Combine(folder, "Shared.2000.1080p.BluRay.x264-GRP.en.srt"), "mine");
        var other = Write(Path.Combine(folder, "Shared.2000.Part.Two.2160p.BluRay.x265-GRP.mkv"));
        await AddMovieByNameAsync(world, "Shared", 2000);

        var response = await world.App.Client.PostAsJsonAsync("/api/integrations/external/redownload", new { path = file, reason = "test", tool = "Weir" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.False(File.Exists(file));
        Assert.False(File.Exists(subtitle));
        Assert.True(File.Exists(other), "the other video stays");
    }

    [Fact]
    public async Task Downloading_a_film_again_in_a_group_folder_leaves_the_other_titles_of_the_group()
    {
        await using var world = await StartWorldAsync();
        var dune = Write(Path.Combine(world.Root, "D", "Dune (2021)", "Dune.2021.1080p.BluRay.x264-GRP.mkv"));
        var dune2 = Write(Path.Combine(world.Root, "D", "Dune Part Two (2024)", "Dune.Part.Two.2024.1080p.BluRay.x264-GRP.mkv"));
        await TrackAsync(world, "Dune", 2021, dune);
        await TrackAsync(world, "Dune Part Two", 2024, dune2);

        var response = await world.App.Client.PostAsJsonAsync("/api/integrations/external/redownload", new { path = dune, reason = "test", tool = "Weir" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.False(File.Exists(dune));
        Assert.True(File.Exists(dune2));
    }

    // ---------------------------------------------------------------- retention

    private static async Task<string> DeleteRuleAsync(RealDiskLibrary world, string level)
    {
        var response = await world.App.Client.PostAsJsonAsync("/api/retention/rules", new
        {
            name = "Old " + level,
            level,
            isEnabled = true,
            steps = new[] { new { type = "condition", op = "keep", condition = "added:beyond:30" } },
            mediaServerIds = Array.Empty<string>(),
            removeFromServers = false,
            waitDays = 1,
            removalMode = "automatic",
            checkIntervalHours = 24,
            expiryAction = "delete",
            expiryTag = (string?)null
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
    }

    private static async Task RunRuleToItsDayAsync(RealDiskLibrary world, string ruleId)
    {
        Assert.True((await world.App.Client.PostAsync($"/api/retention/rules/{ruleId}/check", null)).IsSuccessStatusCode);
        await world.SqlAsync("platform", $"UPDATE retention_marks SET remove_after_utc = '{DateTimeOffset.UtcNow.AddHours(-1):O}';");
        var second = await world.App.Client.PostAsync($"/api/retention/rules/{ruleId}/check", null);
        Assert.True(second.IsSuccessStatusCode, await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_retention_rule_removing_a_film_on_its_day_leaves_the_other_titles_that_share_its_folder_or_its_group()
    {
        await using var world = await StartWorldAsync();
        var folder = Path.Combine(world.Root, "Shared (2000)");
        var film = Write(Path.Combine(folder, "Shared.2000.1080p.BluRay.x264-GRP.mkv"));
        var subtitle = Write(Path.Combine(folder, "Shared.2000.1080p.BluRay.x264-GRP.en.srt"), "mine");
        var other = Write(Path.Combine(folder, "Shared.2000.Part.Two.2160p.BluRay.x265-GRP.mkv"));
        var id = await AddMovieByNameAsync(world, "Shared", 2000);
        var grouped = Write(Path.Combine(world.Root, "G", "Old Film (1950)", "Old.Film.1950.1080p.BluRay.x264-GRP.mkv"));
        var groupedNeighbour = Write(Path.Combine(world.Root, "G", "Other Film (1951)", "Other.Film.1951.1080p.BluRay.x264-GRP.mkv"));
        var groupedId = await TrackAsync(world, "Old Film", 1950, grouped);
        await TrackAsync(world, "Other Film", 1951, groupedNeighbour);
        var old = DateTimeOffset.UtcNow.AddDays(-365);
        await world.SqlAsync("movies", $"UPDATE movie_entries SET created_utc = '{old:O}' WHERE id IN ('{id}', '{groupedId}');");
        var rule = await DeleteRuleAsync(world, "movie");

        await RunRuleToItsDayAsync(world, rule);

        Assert.False(File.Exists(film));
        Assert.False(File.Exists(subtitle));
        Assert.False(File.Exists(grouped));
        Assert.True(File.Exists(other), "the title sharing the folder stays");
        Assert.True(File.Exists(groupedNeighbour), "the title in the same group stays");
        Assert.Equal(HttpStatusCode.NotFound, (await world.App.Client.GetAsync($"/api/movies/{id}")).StatusCode);
    }

    [Fact]
    public async Task A_retention_rule_removing_one_season_leaves_the_shows_other_seasons_and_the_show_folder()
    {
        await using var world = await StartWorldAsync();
        var (libraryId, tvRoot) = await CreateTvLibraryAsync(world.App);
        var show = Path.Combine(tvRoot, "Nosferatu (1922)");
        var one = Write(Path.Combine(show, "Season 01", "Nosferatu.S01E01.1080p.WEB-DL.x264-HD4U.mkv"));
        var oneSubtitle = Write(Path.Combine(show, "Season 01", "Nosferatu.S01E01.1080p.WEB-DL.x264-HD4U.en.srt"), "subtitle");
        var two = Write(Path.Combine(show, "Season 02", "Nosferatu.S02E01.1080p.WEB-DL.x264-HD4U.mkv"));
        var showId = await AddShowAsync(world.App, libraryId);
        // Both seasons have aired, so the seasons can be ranked newest first.
        await world.SqlAsync("series", $"UPDATE episode_entries SET air_date_utc = CASE season_number WHEN 1 THEN '{DateTimeOffset.UtcNow.AddDays(-400):O}' ELSE '{DateTimeOffset.UtcNow.AddDays(-100):O}' END WHERE has_file = 1;");
        var response = await world.App.Client.PostAsJsonAsync("/api/retention/rules", new
        {
            name = "Old seasons", level = "season", isEnabled = true,
            // Keep only the seasons that are not among the newest one.
            steps = new[] { new { type = "condition", op = "keep", condition = "title.seasonRank:min:2" } },
            mediaServerIds = Array.Empty<string>(), removeFromServers = false, waitDays = 1, removalMode = "automatic",
            checkIntervalHours = 24, expiryAction = "delete", expiryTag = (string?)null
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var rule = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;

        await RunRuleToItsDayAsync(world, rule);

        Assert.Equal(HttpStatusCode.OK, (await world.App.Client.GetAsync($"/api/series/{showId}")).StatusCode);
        Assert.False(File.Exists(one), "season one's episode left");
        Assert.False(File.Exists(oneSubtitle));
        Assert.True(File.Exists(two), "season two stays");
        Assert.True(Directory.Exists(show));
    }
}
