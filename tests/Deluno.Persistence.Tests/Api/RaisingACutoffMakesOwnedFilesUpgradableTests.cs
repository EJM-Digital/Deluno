using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Media;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1169: raising a profile's cutoff never made owned files upgradable. "Quality
/// met" is stored, an import is the only thing that raised it, and the upgrade
/// sweep only reads missing and upgrade rows, so 34 WEB 1080p episodes stayed
/// covered after the cutoff moved to WEB 2160p and upgrade-hunting never ran.
///
/// <para>Driven through the same <c>PUT /api/quality-profiles/{id}</c> the
/// profile screen's Save button calls, and read back the way the show page and
/// the sweep read them.</para>
/// </summary>
public sealed class RaisingACutoffMakesOwnedFilesUpgradableTests
{
    private const string Allowed = "WEB 1080p,WEB 2160p";

    // The files on disk are a few bytes, but the catalogue records the size of a real WEB 1080p release. A saved profile judges a file
    // by its size as well as its name (#1334), so a recorded size under the tier's floor would rightly stay an upgrade.
    private const long FilmBytes = 8L * 1024 * 1024 * 1024;
    private const long EpisodeBytes = 3L * 1024 * 1024 * 1024;

    [Fact]
    public async Task Raising_the_cutoff_makes_covered_episodes_upgrades_the_sweep_picks_and_lowering_it_settles_them_again()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var profileId = await CreateProfileAsync(app, "TV 1080p Streaming", "tv", "WEB 1080p");
        var libraryId = await CreateLibraryAsync(app, "tv", profileId);
        var series = app.Services.GetRequiredService<ISeriesCatalogRepository>();
        var seriesId = await ImportShowAsync(app, libraryId, series);

        Assert.All(await EpisodesAsync(series, seriesId), episode =>
        {
            Assert.Equal("covered", episode.WantedStatus);
            Assert.True(episode.QualityCutoffMet);
        });
        Assert.Empty(await EligibleAsync(series, libraryId));

        await SaveProfileAsync(app, profileId, "WEB 2160p");

        var raised = await EpisodesAsync(series, seriesId);
        Assert.All(raised, episode =>
        {
            Assert.Equal("upgrade", episode.WantedStatus);
            Assert.False(episode.QualityCutoffMet);
            Assert.Equal("WEB 2160p", episode.TargetQuality);
        });
        Assert.Equal(2, (await EligibleAsync(series, libraryId)).Count);
        // The show page, the Upgrades card and the Upgradable pill read the same answer.
        Assert.Equal("upgrade", (await series.GetByIdAsync(seriesId, CancellationToken.None))!.WantedStatus);

        await SaveProfileAsync(app, profileId, "WEB 1080p");

        Assert.All(await EpisodesAsync(series, seriesId), episode => Assert.Equal("covered", episode.WantedStatus));
        Assert.Empty(await EligibleAsync(series, libraryId));
        Assert.Equal("covered", (await series.GetByIdAsync(seriesId, CancellationToken.None))!.WantedStatus);
    }

    [Fact]
    public async Task Raising_the_cutoff_makes_a_covered_film_an_upgrade_and_lowering_it_settles_it_again()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var profileId = await CreateProfileAsync(app, "Movies 1080p", "movies", "WEB 1080p");
        var libraryId = await CreateLibraryAsync(app, "movies", profileId);
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        var mediaState = app.Services.GetRequiredService<IMediaStateRepository>();
        var movieId = await ImportFilmAsync(app, libraryId, movies);

        Assert.Equal("covered", (await WantedAsync(mediaState, movieId)).WantedStatus);

        await SaveProfileAsync(app, profileId, "WEB 2160p");

        var raised = await WantedAsync(mediaState, movieId);
        Assert.Equal("upgrade", raised.WantedStatus);
        Assert.False(raised.QualityCutoffMet);
        Assert.Equal("WEB 2160p", raised.TargetQuality);
        Assert.Single(await movies.ListEligibleWantedAsync(libraryId, 10, DateTimeOffset.UtcNow, true, CancellationToken.None));

        await SaveProfileAsync(app, profileId, "WEB 1080p");

        Assert.Equal("covered", (await WantedAsync(mediaState, movieId)).WantedStatus);
        Assert.Empty(await movies.ListEligibleWantedAsync(libraryId, 10, DateTimeOffset.UtcNow, true, CancellationToken.None));
    }

    [Fact]
    public async Task Giving_a_title_a_profile_of_its_own_judges_it_by_that_profile_and_leaves_its_neighbours_alone()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryProfile = await CreateProfileAsync(app, "Movies 1080p", "movies", "WEB 1080p");
        var fourK = await CreateProfileAsync(app, "Movies 4K Streaming", "movies", "WEB 2160p");
        var libraryId = await CreateLibraryAsync(app, "movies", libraryProfile);
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        var mediaState = app.Services.GetRequiredService<IMediaStateRepository>();
        var matrix = await ImportFilmAsync(app, libraryId, movies, "The Matrix", 1999);
        var dune = await ImportFilmAsync(app, libraryId, movies, "Dune", 2021);

        var response = await app.Client.PostAsJsonAsync(
            "/api/movies/bulk/quality-profile",
            new { movieIds = new[] { matrix }, qualityProfileId = fourK });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        // The Matrix is judged by the 4K profile at once: a 1080p file is short of it.
        var own = await WantedAsync(mediaState, matrix);
        Assert.Equal("upgrade", own.WantedStatus);
        Assert.Equal("WEB 2160p", own.TargetQuality);
        // Its neighbour, with no profile of its own, still follows the library.
        var neighbour = await WantedAsync(mediaState, dune);
        Assert.Equal("covered", neighbour.WantedStatus);
        Assert.Equal("WEB 1080p", neighbour.TargetQuality);

        // Editing the library's profile re-decides the neighbour and leaves the title that chose its own.
        await SaveProfileAsync(app, libraryProfile, "WEB 2160p");
        Assert.Equal("upgrade", (await WantedAsync(mediaState, dune)).WantedStatus);
        await SaveProfileAsync(app, libraryProfile, "WEB 1080p");
        Assert.Equal("covered", (await WantedAsync(mediaState, dune)).WantedStatus);
        var stillOwn = await WantedAsync(mediaState, matrix);
        Assert.Equal("upgrade", stillOwn.WantedStatus);
        Assert.Equal("WEB 2160p", stillOwn.TargetQuality);

        // And editing the profile the title chose re-decides the title.
        await SaveProfileAsync(app, fourK, "WEB 1080p");
        Assert.Equal("covered", (await WantedAsync(mediaState, matrix)).WantedStatus);
    }

    [Fact]
    public async Task Sending_no_profile_puts_a_title_back_on_its_librarys_profile_for_films_and_shows()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var movieLibraryProfile = await CreateProfileAsync(app, "Movies 1080p", "movies", "WEB 1080p");
        var movieFourK = await CreateProfileAsync(app, "Movies 4K Streaming", "movies", "WEB 2160p");
        var movieLibrary = await CreateLibraryAsync(app, "movies", movieLibraryProfile);
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        var mediaState = app.Services.GetRequiredService<IMediaStateRepository>();
        var movieId = await ImportFilmAsync(app, movieLibrary, movies);

        var set = await app.Client.PostAsJsonAsync("/api/movies/bulk/quality-profile", new { movieIds = new[] { movieId }, qualityProfileId = movieFourK });
        Assert.True(set.IsSuccessStatusCode);
        Assert.Equal("upgrade", (await WantedAsync(mediaState, movieId)).WantedStatus);

        var cleared = await app.Client.PostAsJsonAsync("/api/movies/bulk/quality-profile", new { movieIds = new[] { movieId }, qualityProfileId = (string?)null });
        Assert.True(cleared.IsSuccessStatusCode, await cleared.Content.ReadAsStringAsync());
        Assert.Null(await mediaState.GetTitleQualityProfileIdAsync(MediaKind.Movie, movieId, CancellationToken.None));
        var back = await WantedAsync(mediaState, movieId);
        Assert.Equal("covered", back.WantedStatus);
        Assert.Equal("WEB 1080p", back.TargetQuality);

        var tvLibraryProfile = await CreateProfileAsync(app, "TV 1080p Streaming", "tv", "WEB 1080p");
        var tvFourK = await CreateProfileAsync(app, "TV / 4K Streaming", "tv", "WEB 2160p");
        var tvLibrary = await CreateLibraryAsync(app, "tv", tvLibraryProfile);
        var series = app.Services.GetRequiredService<ISeriesCatalogRepository>();
        var seriesId = await ImportShowAsync(app, tvLibrary, series);

        await app.Client.PostAsJsonAsync("/api/series/bulk/quality-profile", new { seriesIds = new[] { seriesId }, qualityProfileId = tvFourK });
        Assert.All(await EpisodesAsync(series, seriesId), episode => Assert.Equal("upgrade", episode.WantedStatus));

        var clearedShow = await app.Client.PostAsJsonAsync("/api/series/bulk/quality-profile", new { seriesIds = new[] { seriesId }, qualityProfileId = (string?)null });
        Assert.True(clearedShow.IsSuccessStatusCode, await clearedShow.Content.ReadAsStringAsync());
        Assert.Null(await mediaState.GetTitleQualityProfileIdAsync(MediaKind.Series, seriesId, CancellationToken.None));
        Assert.All(await EpisodesAsync(series, seriesId), episode =>
        {
            Assert.Equal("covered", episode.WantedStatus);
            Assert.Equal("WEB 1080p", episode.TargetQuality);
        });
    }

    [Fact]
    public async Task Giving_a_library_another_profile_rechecks_its_titles_and_refuses_a_profile_for_the_other_kind_of_media()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var modest = await CreateProfileAsync(app, "Movies 1080p", "movies", "WEB 1080p");
        var ambitious = await CreateProfileAsync(app, "Movies 4K", "movies", "WEB 2160p");
        var tv = await CreateProfileAsync(app, "TV 1080p", "tv", "WEB 1080p");
        var libraryId = await CreateLibraryAsync(app, "movies", modest);
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        var mediaState = app.Services.GetRequiredService<IMediaStateRepository>();
        var movieId = await ImportFilmAsync(app, libraryId, movies);
        Assert.Equal("covered", (await WantedAsync(mediaState, movieId)).WantedStatus);

        var wrongKind = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/quality-profile", new { qualityProfileId = tv });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, wrongKind.StatusCode);
        Assert.Equal("covered", (await WantedAsync(mediaState, movieId)).WantedStatus);

        var response = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/quality-profile", new { qualityProfileId = ambitious });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Equal(ambitious, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("qualityProfileId").GetString());

        var raised = await WantedAsync(mediaState, movieId);
        Assert.Equal("upgrade", raised.WantedStatus);
        Assert.Equal("WEB 2160p", raised.TargetQuality);

        await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/quality-profile", new { qualityProfileId = modest });
        Assert.Equal("covered", (await WantedAsync(mediaState, movieId)).WantedStatus);
    }

    [Fact]
    public async Task A_cutoff_change_is_written_to_Activity_in_plain_words()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var profileId = await CreateProfileAsync(app, "Movies 1080p", "movies", "WEB 1080p");
        var libraryId = await CreateLibraryAsync(app, "movies", profileId);
        await ImportFilmAsync(app, libraryId, app.Services.GetRequiredService<IMovieCatalogRepository>());

        await SaveProfileAsync(app, profileId, "WEB 2160p");

        var activity = await app.Client.GetStringAsync("/api/activity");
        Assert.Contains("rechecked what you own", activity, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1 can now be upgraded", activity, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<IReadOnlyList<SeriesEpisodeInventoryItem>> EpisodesAsync(ISeriesCatalogRepository series, string seriesId)
        => (await series.GetInventoryDetailAsync(seriesId, CancellationToken.None))!.Episodes;

    private static Task<IReadOnlyList<EpisodeSearchEligibilityItem>> EligibleAsync(ISeriesCatalogRepository series, string libraryId)
        => series.ListEligibleWantedEpisodesAsync(libraryId, 50, DateTimeOffset.UtcNow, true, CancellationToken.None);

    private static async Task<MediaWantedItem> WantedAsync(IMediaStateRepository mediaState, string movieId)
        => Assert.Single(await mediaState.ListWantedByIdsAsync(MediaKind.Movie, [movieId], CancellationToken.None));

    private static async Task<string> ImportShowAsync(ApplicationTestHost app, string libraryId, ISeriesCatalogRepository series)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Shows", "Chernobyl")).FullName;
        var episodes = new List<ImportedEpisodeItem>();
        foreach (var number in new[] { 1, 2 })
        {
            var path = Path.Combine(root, $"Chernobyl S01E0{number} WEB 1080p.mkv");
            await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
            episodes.Add(new ImportedEpisodeItem(
                1, number, HasFile: true, FilePath: path, FileSizeBytes: EpisodeBytes,
                AirDate: new DateOnly(2019, 5, 6 + number), CurrentQuality: "WEB 1080p", QualityCutoffMet: true));
        }

        await series.ImportExistingAsync(
            libraryId, "Chernobyl", 2019,
            wantedStatus: "covered", wantedReason: "Imported", currentQuality: "WEB 1080p", targetQuality: "WEB 1080p",
            qualityCutoffMet: true, filePath: null, fileSizeBytes: null, episodes: episodes,
            cancellationToken: CancellationToken.None, upgradeUntilCutoff: true);
        var seriesId = Assert.Single(await series.ListAsync(CancellationToken.None)).Id;
        // The air dates the catalogue would carry; the rung counts aired episodes.
        await using var connection = await app.Services.GetRequiredService<Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory>()
            .OpenConnectionAsync("series", CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE episode_entries SET air_date_utc = '2019-05-07T00:00:00.0000000+00:00' WHERE series_id = @id;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = seriesId;
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync();
        return seriesId;
    }

    private static async Task<string> ImportFilmAsync(
        ApplicationTestHost app, string libraryId, IMovieCatalogRepository movies, string title = "Dune", int year = 2021)
    {
        var path = Path.Combine(Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films", $"{title} ({year})")).FullName, $"{title} ({year}) WEB 1080p.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        await movies.ImportExistingAsync(
            libraryId, title, year,
            wantedStatus: "covered", wantedReason: "Imported", currentQuality: "WEB 1080p", targetQuality: "WEB 1080p",
            qualityCutoffMet: true, filePath: path, fileSizeBytes: FilmBytes, cancellationToken: CancellationToken.None);
        return (await movies.ListAsync(CancellationToken.None)).First(item => item.Title == title).Id;
    }

    private static async Task<string> CreateProfileAsync(ApplicationTestHost app, string name, string mediaType, string cutoff)
    {
        var response = await app.Client.PostAsJsonAsync("/api/quality-profiles", new
        {
            name,
            mediaType,
            cutoffQuality = cutoff,
            allowedQualities = Allowed,
            customFormatIds = string.Empty,
            upgradeUntilCutoff = true,
            upgradeUnknownItems = false
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
    }

    private static async Task SaveProfileAsync(ApplicationTestHost app, string profileId, string cutoff)
    {
        var listed = JsonDocument.Parse(await app.Client.GetStringAsync("/api/quality-profiles")).RootElement
            .EnumerateArray().First(item => item.GetProperty("id").GetString() == profileId);
        var response = await app.Client.PutAsJsonAsync($"/api/quality-profiles/{profileId}", new
        {
            name = listed.GetProperty("name").GetString(),
            cutoffQuality = cutoff,
            allowedQualities = Allowed,
            customFormatIds = string.Empty,
            upgradeUntilCutoff = true,
            upgradeUnknownItems = false
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> CreateLibraryAsync(ApplicationTestHost app, string mediaType, string profileId)
    {
        var response = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name = mediaType == "tv" ? "Shows" : "Films",
            mediaType,
            purpose = "collection",
            rootPath = Directory.CreateDirectory(Path.Combine(app.DataRoot, mediaType == "tv" ? "Shows" : "Films")).FullName,
            downloadsPath = (string?)null,
            qualityProfileId = profileId,
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
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
    }
}
