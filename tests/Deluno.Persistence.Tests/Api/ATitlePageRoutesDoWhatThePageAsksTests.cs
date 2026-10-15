using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The routes a film page and a show page read and write (coverage register, areas movie-detail and show-detail), over the real
/// host with real SQLite files and real files on disk: what the page is told, what it may change, and what each refusal says.
/// </summary>
public sealed class ATitlePageRoutesDoWhatThePageAsksTests
{
    // ------------------------------------------------------------------------------------------- the film

    [Fact]
    public async Task A_film_is_read_with_the_fields_the_page_draws_and_an_unknown_film_is_a_not_found()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021);

        var body = await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{film.MovieId}");

        Assert.Equal("Dune", body.GetProperty("title").GetString());
        Assert.Equal(2021, body.GetProperty("releaseYear").GetInt32());
        Assert.True(body.GetProperty("hasFile").GetBoolean());
        Assert.Equal(film.LibraryId, body.GetProperty("libraryId").GetString());
        Assert.Equal("covered", body.GetProperty("wantedStatus").GetString());
        Assert.Equal("Bluray-1080p", body.GetProperty("currentQuality").GetString());
        foreach (var field in new[] { "monitored", "metadataJson", "targetQuality", "qualityCutoffMet", "rating", "genres" })
        {
            Assert.True(body.TryGetProperty(field, out _), $"the film answer lacks {field}");
        }

        using var unknown = await app.Client.GetAsync("/api/movies/no-such-film");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Changing_monitoring_applies_to_every_film_chosen_and_choosing_none_is_refused_with_the_reason()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var first = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021);
        var second = await ApiScenes.FilmOnDiskAsync(app, "Heat", 1995, first.LibraryId);

        var off = await app.Client.PutAsJsonAsync("/api/movies/monitoring", new { movieIds = new[] { first.MovieId, second.MovieId }, monitored = false });

        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.Equal(2, (await off.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("updated").GetInt32());
        foreach (var id in new[] { first.MovieId, second.MovieId })
        {
            Assert.False((await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{id}")).GetProperty("monitored").GetBoolean());
        }

        var on = await app.Client.PutAsJsonAsync("/api/movies/monitoring", new { movieIds = new[] { first.MovieId }, monitored = true });
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.True((await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{first.MovieId}")).GetProperty("monitored").GetBoolean());
        Assert.False((await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{second.MovieId}")).GetProperty("monitored").GetBoolean());

        var none = await app.Client.PutAsJsonAsync("/api/movies/monitoring", new { movieIds = Array.Empty<string>(), monitored = true });
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Contains("Choose at least one movie", await none.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_downgrade_protection_flag_is_stored_and_read_back_in_the_workflow_status_and_an_unknown_film_is_a_not_found()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(app);

        var before = await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{film.MovieId}/workflow-status");
        Assert.Equal("Bluray-1080p", before.GetProperty("currentQuality").GetString());
        Assert.Equal("Bluray-1080p", before.GetProperty("targetQuality").GetString());
        var started = before.GetProperty("preventLowerQualityReplacements").GetBoolean();

        var flipped = await app.Client.PutAsJsonAsync($"/api/movies/{film.MovieId}/replacement-protection", new { preventLowerQualityReplacements = !started });
        Assert.Equal(HttpStatusCode.NoContent, flipped.StatusCode);
        var after = await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{film.MovieId}/workflow-status");
        Assert.Equal(!started, after.GetProperty("preventLowerQualityReplacements").GetBoolean());

        using var unknownRead = await app.Client.GetAsync("/api/movies/no-such-film/workflow-status");
        Assert.Equal(HttpStatusCode.NotFound, unknownRead.StatusCode);
        var unknownWrite = await app.Client.PutAsJsonAsync("/api/movies/no-such-film/replacement-protection", new { preventLowerQualityReplacements = true });
        Assert.Equal(HttpStatusCode.NotFound, unknownWrite.StatusCode);
    }

    [Fact]
    public async Task Deferring_a_film_needs_its_library_clamps_the_hours_and_an_unknown_film_is_a_not_found()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await WantedFilmAsync(app);

        var noLibrary = await app.Client.PostAsJsonAsync($"/api/movies/{film.MovieId}/automation/defer", new { libraryId = "", hours = 24 });
        Assert.Equal(HttpStatusCode.BadRequest, noLibrary.StatusCode);
        Assert.Contains("not attached to an automated library", await noLibrary.Content.ReadAsStringAsync());

        var unknown = await app.Client.PostAsJsonAsync("/api/movies/no-such-film/automation/defer", new { libraryId = film.LibraryId, hours = 24 });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var now = DateTimeOffset.UtcNow;
        var tooLong = await app.Client.PostAsJsonAsync($"/api/movies/{film.MovieId}/automation/defer", new { libraryId = film.LibraryId, hours = 5000 });
        Assert.Equal(HttpStatusCode.OK, tooLong.StatusCode);
        var until = (await tooLong.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("deferredUntilUtc").GetDateTimeOffset();
        Assert.InRange((until - now).TotalHours, 719, 721);

        var tooShort = await app.Client.PostAsJsonAsync($"/api/movies/{film.MovieId}/automation/defer", new { libraryId = film.LibraryId, hours = 0 });
        var shortUntil = (await tooShort.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("deferredUntilUtc").GetDateTimeOffset();
        Assert.InRange((shortUntil - DateTimeOffset.UtcNow).TotalHours, 0.9, 1.1);
    }

    [Fact]
    public async Task Skipping_the_next_search_of_a_film_needs_its_library_and_an_unknown_film_is_a_not_found()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await WantedFilmAsync(app);

        var noLibrary = await app.Client.PostAsJsonAsync($"/api/movies/{film.MovieId}/automation/skip-once", new { libraryId = "" });
        Assert.Equal(HttpStatusCode.BadRequest, noLibrary.StatusCode);

        var unknown = await app.Client.PostAsJsonAsync("/api/movies/no-such-film/automation/skip-once", new { libraryId = film.LibraryId });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var skipped = await app.Client.PostAsJsonAsync($"/api/movies/{film.MovieId}/automation/skip-once", new { libraryId = film.LibraryId });
        Assert.Equal(HttpStatusCode.OK, skipped.StatusCode);
        Assert.Contains("will be skipped", await skipped.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_removal_preview_of_a_film_lists_its_file_and_its_folder_and_an_unknown_film_is_a_not_found()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(app);

        var preview = await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{film.MovieId}/removal-preview");

        Assert.Contains(preview.GetProperty("filePaths").EnumerateArray().Select(item => item.GetString()), path => SamePath(path, film.FilePath));
        Assert.Contains(preview.GetProperty("folderPaths").EnumerateArray().Select(item => item.GetString()), path => SamePath(path, film.FolderPath));
        Assert.True(File.Exists(film.FilePath), "looking at what removal would touch removes nothing");

        using var unknown = await app.Client.GetAsync("/api/movies/no-such-film/removal-preview");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task A_films_own_older_search_is_still_in_the_list_its_page_reads_after_twenty_five_newer_searches_for_other_films()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021);
        var other = await ApiScenes.FilmOnDiskAsync(app, "Heat", 1995, film.LibraryId);
        Assert.True(File.Exists(film.FilePath), "the film is on a real disk, in a real library folder");
        await InsertMovieSearchAsync(app, film, film.MovieId, "Dune.2021.1080p.BluRay-OWN", DateTimeOffset.UtcNow.AddDays(-2));
        for (var index = 0; index < 25; index++)
        {
            await InsertMovieSearchAsync(app, film, other.MovieId, $"Heat.1995.Release.{index}", DateTimeOffset.UtcNow.AddMinutes(-index));
        }

        var history = await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/search-history?movieId={film.MovieId}");

        Assert.Contains(history.EnumerateArray(), item => item.GetProperty("movieId").GetString() == film.MovieId);
        Assert.All(history.EnumerateArray(), item => Assert.Equal(film.MovieId, item.GetProperty("movieId").GetString()));
    }

    [Fact]
    public async Task A_titles_search_history_is_paged_on_the_server_for_a_film_and_for_a_show()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021);
        var show = await ApiScenes.ShowOnDiskAsync(app);
        Assert.True(File.Exists(film.FilePath) && File.Exists(show.FilePath), "both titles are on a real disk, in real library folders");
        for (var index = 0; index < 5; index++)
        {
            await InsertMovieSearchAsync(app, film, film.MovieId, $"Dune.2021.Release.{index}", DateTimeOffset.UtcNow.AddMinutes(-index));
            await InsertSeriesSearchAsync(app, show, $"The.Wire.S01E01.Release.{index}", "manual", DateTimeOffset.UtcNow.AddMinutes(-index));
        }

        var filmPage = await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/search-history?movieId={film.MovieId}&take=2&skip=1");
        var showPage = await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/search-history?seriesId={show.SeriesId}&take=2&skip=1");

        Assert.Equal(["Dune.2021.Release.1", "Dune.2021.Release.2"], filmPage.EnumerateArray().Select(item => item.GetProperty("releaseName").GetString()));
        Assert.Equal(["The.Wire.S01E01.Release.1", "The.Wire.S01E01.Release.2"], showPage.EnumerateArray().Select(item => item.GetProperty("releaseName").GetString()));
    }

    // ------------------------------------------------------------------------------------------- the show

    [Fact]
    public async Task A_shows_numbering_is_read_with_its_type_scheme_source_and_every_episode_and_an_unknown_show_is_a_not_found()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var show = await ApiScenes.ShowOnDiskAsync(app);

        var numbering = await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/{show.SeriesId}/numbering");

        Assert.Equal(show.SeriesId, numbering.GetProperty("seriesId").GetString());
        Assert.Equal("standard", numbering.GetProperty("seriesType").GetString());
        Assert.Equal("standard", numbering.GetProperty("numberingScheme").GetString());
        Assert.Equal("provider", numbering.GetProperty("numberingSource").GetString());
        Assert.Contains("standard", numbering.GetProperty("availableNumberingSchemes").EnumerateArray().Select(item => item.GetString()));
        var episode = Assert.Single(numbering.GetProperty("episodes").EnumerateArray());
        Assert.Equal(show.EpisodeId, episode.GetProperty("episodeId").GetString());

        using var unknown = await app.Client.GetAsync("/api/series/no-such-show/numbering");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task An_episodes_file_is_read_with_its_path_size_release_group_indexer_and_client_and_a_missing_one_is_a_not_found()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var show = await ApiScenes.ShowOnDiskAsync(app);
        await ApiScenes.ExecuteAsync(app, "jobs",
            $"INSERT INTO download_dispatches (id, library_id, media_type, entity_type, entity_id, release_name, indexer_name, download_client_id, download_client_name, status, created_utc) VALUES ('{Guid.NewGuid():N}', '{show.LibraryId}', 'tv', 'episode', '{show.EpisodeId}', 'The.Wire.S01E01.1080p.WEB-DL.H264-EXAMPLE', 'Fixture Indexer', 'client-1', 'Fixture Client', 'sent', '{DateTimeOffset.UtcNow:O}');");

        var file = await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/episodes/{show.EpisodeId}/file");

        Assert.Equal(show.FilePath, file.GetProperty("filePath").GetString());
        Assert.True(file.GetProperty("fileSizeBytes").GetInt64() > 0);
        Assert.Equal("Fixture Indexer", file.GetProperty("indexerName").GetString());
        Assert.Equal("Fixture Client", file.GetProperty("downloadClientName").GetString());
        Assert.Equal("EXAMPLE", file.GetProperty("releaseGroup").GetString());

        using var unknown = await app.Client.GetAsync("/api/series/episodes/no-such-episode/file");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Theory]
    [InlineData("""{"seriesType":"cartoon"}""", "seriesType", "Series type must be standard, daily, or anime.")]
    [InlineData("""{"numberingScheme":"alphabetical"}""", "numberingScheme", "Numbering scheme must be standard, airdate, absolute, dvd, or scene.")]
    [InlineData("""{"numberingSource":"guess"}""", "numberingSource", "Numbering source must be provider or owner.")]
    public async Task Saving_numbering_with_an_unknown_type_scheme_or_source_is_refused_with_its_own_message_and_changes_nothing(string json, string field, string message)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var show = await ApiScenes.ShowOnDiskAsync(app);

        var refused = await PutNumberingAsync(app, show.SeriesId, json);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var errors = (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Equal(message, errors.GetProperty(field).EnumerateArray().Single().GetString());
        var unchanged = await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/{show.SeriesId}/numbering");
        Assert.Equal("standard", unchanged.GetProperty("seriesType").GetString());
        Assert.Equal("provider", unchanged.GetProperty("numberingSource").GetString());
    }

    [Fact]
    public async Task Saving_numbering_mappings_refuses_a_duplicate_episode_an_absolute_number_out_of_range_and_half_a_scene_number()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var show = await ApiScenes.ShowOnDiskAsync(app);
        var episode = show.EpisodeId;

        var duplicate = await PutNumberingAsync(app, show.SeriesId, $$"""{"numberingSource":"owner","mappings":[{"episodeId":"{{episode}}","absoluteNumber":1},{"episodeId":"{{episode}}","absoluteNumber":2}]}""");
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        Assert.Contains("Each episode may appear only once in a numbering mapping.", await duplicate.Content.ReadAsStringAsync());

        var zero = await PutNumberingAsync(app, show.SeriesId, $$"""{"numberingSource":"owner","mappings":[{"episodeId":"{{episode}}","absoluteNumber":0}]}""");
        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);
        Assert.Contains("Absolute episode numbers must be between 1 and 9999.", await zero.Content.ReadAsStringAsync());

        var tooBig = await PutNumberingAsync(app, show.SeriesId, $$"""{"numberingSource":"owner","mappings":[{"episodeId":"{{episode}}","absoluteNumber":10000}]}""");
        Assert.Equal(HttpStatusCode.BadRequest, tooBig.StatusCode);

        var halfScene = await PutNumberingAsync(app, show.SeriesId, $$"""{"numberingSource":"owner","mappings":[{"episodeId":"{{episode}}","sceneSeasonNumber":2}]}""");
        Assert.Equal(HttpStatusCode.BadRequest, halfScene.StatusCode);
        Assert.Contains("Scene season and episode numbers must be supplied together.", await halfScene.Content.ReadAsStringAsync());

        var noEpisode = await PutNumberingAsync(app, show.SeriesId, """{"numberingSource":"owner","mappings":[{"episodeId":"","absoluteNumber":3}]}""");
        Assert.Equal(HttpStatusCode.BadRequest, noEpisode.StatusCode);
        Assert.Contains("A numbering mapping must identify an episode.", await noEpisode.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Saving_an_owner_mapping_stores_it_and_reads_it_back_and_a_show_that_is_not_there_is_a_not_found()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var show = await ApiScenes.ShowOnDiskAsync(app);

        var saved = await PutNumberingAsync(app, show.SeriesId, $$"""{"seriesType":"anime","numberingScheme":"absolute","numberingSource":"owner","mappings":[{"episodeId":"{{show.EpisodeId}}","absoluteNumber":105,"sceneSeasonNumber":2,"sceneEpisodeNumber":5}]}""");

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var numbering = await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/{show.SeriesId}/numbering");
        Assert.Equal("anime", numbering.GetProperty("seriesType").GetString());
        Assert.Equal("absolute", numbering.GetProperty("numberingScheme").GetString());
        Assert.Equal("owner", numbering.GetProperty("numberingSource").GetString());
        var mapped = Assert.Single(numbering.GetProperty("episodes").EnumerateArray());
        Assert.Equal(105, mapped.GetProperty("absoluteNumber").GetInt32());
        Assert.Equal(2, mapped.GetProperty("sceneSeasonNumber").GetInt32());
        Assert.Equal(5, mapped.GetProperty("sceneEpisodeNumber").GetInt32());

        var unknown = await PutNumberingAsync(app, "no-such-show", """{"seriesType":"anime"}""");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task A_shows_inventory_lists_its_seasons_and_episodes_with_wanted_state_and_numbering_and_an_unknown_show_is_a_not_found()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var show = await ApiScenes.ShowOnDiskAsync(app);
        var catalogue = app.Services.GetRequiredService<ISeriesCatalogRepository>();
        await catalogue.SyncEpisodeCatalogueAsync(
            show.SeriesId,
            [
                new CatalogueEpisodeItem(0, 1, "A Special", null, DateTimeOffset.UtcNow.AddDays(-30)),
                new CatalogueEpisodeItem(1, 1, "Pilot", null, DateTimeOffset.UtcNow.AddDays(-30)),
                new CatalogueEpisodeItem(1, 2, "Second", null, DateTimeOffset.UtcNow.AddDays(-20)),
                new CatalogueEpisodeItem(2, 1, "Not Yet", null, DateTimeOffset.UtcNow.AddDays(30))
            ],
            "tmdb",
            CancellationToken.None);

        var inventory = await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/{show.SeriesId}/inventory");

        Assert.Equal(show.SeriesId, inventory.GetProperty("seriesId").GetString());
        Assert.Equal("The Wire", inventory.GetProperty("title").GetString());
        Assert.Equal(3, inventory.GetProperty("seasonCount").GetInt32());
        Assert.Equal(4, inventory.GetProperty("episodeCount").GetInt32());
        Assert.Equal(1, inventory.GetProperty("importedEpisodeCount").GetInt32());
        var episodes = inventory.GetProperty("episodes").EnumerateArray().ToArray();
        var pilot = episodes.Single(item => item.GetProperty("seasonNumber").GetInt32() == 1 && item.GetProperty("episodeNumber").GetInt32() == 1);
        Assert.True(pilot.GetProperty("hasFile").GetBoolean());
        Assert.Equal("Pilot", pilot.GetProperty("title").GetString());
        var second = episodes.Single(item => item.GetProperty("seasonNumber").GetInt32() == 1 && item.GetProperty("episodeNumber").GetInt32() == 2);
        Assert.False(second.GetProperty("hasFile").GetBoolean());
        Assert.Equal("missing", second.GetProperty("wantedStatus").GetString());
        Assert.Contains(episodes, item => item.GetProperty("seasonNumber").GetInt32() == 0);
        Assert.Equal("standard", inventory.GetProperty("numbering").GetProperty("numberingScheme").GetString());
        Assert.True(File.Exists(show.FilePath), "reading the inventory leaves the episode's file where it is");

        using var unknown = await app.Client.GetAsync("/api/series/no-such-show/inventory");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task A_shows_search_history_lists_what_was_searched_newest_first_with_its_episode_numbers()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var show = await ApiScenes.ShowOnDiskAsync(app);
        await InsertSeriesSearchAsync(app, show, "The.Wire.S01E01.Old", "manual", DateTimeOffset.UtcNow.AddDays(-1));
        await InsertSeriesSearchAsync(app, show, "The.Wire.S01E01.New", "scheduled", DateTimeOffset.UtcNow);

        var history = (await app.Client.GetFromJsonAsync<JsonElement>("/api/series/search-history")).EnumerateArray().ToArray();

        Assert.Equal(2, history.Length);
        Assert.Equal("The.Wire.S01E01.New", history[0].GetProperty("releaseName").GetString());
        Assert.Equal("scheduled", history[0].GetProperty("triggerKind").GetString());
        Assert.Equal(show.SeriesId, history[0].GetProperty("seriesId").GetString());
        Assert.Equal(1, history[0].GetProperty("seasonNumber").GetInt32());
        Assert.Equal(1, history[0].GetProperty("episodeNumber").GetInt32());
        Assert.Equal("manual", history[1].GetProperty("triggerKind").GetString());
    }

    [Fact]
    public async Task The_removal_preview_of_a_show_lists_the_files_and_folders_removal_would_touch_and_touches_none_of_them()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var show = await ApiScenes.ShowOnDiskAsync(app);

        var preview = await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/{show.SeriesId}/removal-preview");

        Assert.Contains(preview.GetProperty("filePaths").EnumerateArray().Select(item => item.GetString()), path => SamePath(path, show.FilePath));
        Assert.Contains(preview.GetProperty("folderPaths").EnumerateArray().Select(item => item.GetString()), path => SamePath(path, show.FolderPath));
        Assert.True(File.Exists(show.FilePath));

        using var unknown = await app.Client.GetAsync("/api/series/no-such-show/removal-preview");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Searching_for_episodes_with_none_chosen_is_refused_an_unknown_show_is_a_not_found_and_an_episode_that_is_not_tracked_is_refused()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var show = await ApiScenes.ShowOnDiskAsync(app);

        var none = await app.Client.PostAsJsonAsync($"/api/series/{show.SeriesId}/episodes/search", new { episodeIds = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Contains("Choose at least one episode", await none.Content.ReadAsStringAsync());

        var unknown = await app.Client.PostAsJsonAsync("/api/series/no-such-show/episodes/search", new { episodeIds = new[] { show.EpisodeId } });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var notThere = await app.Client.PostAsJsonAsync($"/api/series/{show.SeriesId}/episodes/search", new { episodeIds = new[] { "not-an-episode" } });
        Assert.Equal(HttpStatusCode.BadRequest, notThere.StatusCode);
        Assert.Contains("could not find those episodes", await notThere.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Pausing_and_resuming_a_show_is_stored_and_choosing_no_show_is_refused()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var show = await ApiScenes.ShowOnDiskAsync(app);

        var off = await app.Client.PutAsJsonAsync("/api/series/monitoring", new { seriesIds = new[] { show.SeriesId }, monitored = false });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.False((await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/{show.SeriesId}")).GetProperty("monitored").GetBoolean());

        var on = await app.Client.PutAsJsonAsync("/api/series/monitoring", new { seriesIds = new[] { show.SeriesId }, monitored = true });
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.True((await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/{show.SeriesId}")).GetProperty("monitored").GetBoolean());

        var none = await app.Client.PutAsJsonAsync("/api/series/monitoring", new { seriesIds = Array.Empty<string>(), monitored = false });
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Contains("Choose at least one series", await none.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Deferring_and_skipping_a_shows_search_need_its_library_and_an_unknown_show_is_a_not_found()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var show = await ApiScenes.ShowOnDiskAsync(app);

        var noLibrary = await app.Client.PostAsJsonAsync($"/api/series/{show.SeriesId}/automation/defer", new { libraryId = "", hours = 24 });
        Assert.Equal(HttpStatusCode.BadRequest, noLibrary.StatusCode);
        var noLibrarySkip = await app.Client.PostAsJsonAsync($"/api/series/{show.SeriesId}/automation/skip-once", new { libraryId = "" });
        Assert.Equal(HttpStatusCode.BadRequest, noLibrarySkip.StatusCode);

        var unknown = await app.Client.PostAsJsonAsync("/api/series/no-such-show/automation/defer", new { libraryId = show.LibraryId, hours = 24 });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        var unknownSkip = await app.Client.PostAsJsonAsync("/api/series/no-such-show/automation/skip-once", new { libraryId = show.LibraryId });
        Assert.Equal(HttpStatusCode.NotFound, unknownSkip.StatusCode);

        var now = DateTimeOffset.UtcNow;
        var deferred = await app.Client.PostAsJsonAsync($"/api/series/{show.SeriesId}/automation/defer", new { libraryId = show.LibraryId, hours = 24 });
        Assert.Equal(HttpStatusCode.OK, deferred.StatusCode);
        var until = (await deferred.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("deferredUntilUtc").GetDateTimeOffset();
        Assert.InRange((until - now).TotalHours, 23, 25);

        var skipped = await app.Client.PostAsJsonAsync($"/api/series/{show.SeriesId}/automation/skip-once", new { libraryId = show.LibraryId });
        Assert.Equal(HttpStatusCode.OK, skipped.StatusCode);
    }

    // ------------------------------------------------------------------------------------------- helpers

    /// <summary>A film nothing has found yet, so its wanted state is "missing" and a defer or skip has a search to hold back.</summary>
    private static async Task<(string MovieId, string LibraryId)> WantedFilmAsync(ApplicationTestHost app)
    {
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var added = await app.Client.PostAsJsonAsync("/api/movies/", new { title = "Big Buck Bunny", releaseYear = 2008, monitored = true, libraryId, searchForMissingNow = false });
        Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync());
        var movieId = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        Assert.Empty(Directory.GetFiles(await ApiScenes.LibraryRootAsync(app, libraryId), "*", SearchOption.AllDirectories));
        return (movieId, libraryId);
    }

    private static Task<HttpResponseMessage> PutNumberingAsync(ApplicationTestHost app, string seriesId, string json)
        => app.Client.PutAsync($"/api/series/{seriesId}/numbering", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

    private static bool SamePath(string? left, string right)
        => string.Equals(Path.GetFullPath(left ?? string.Empty).TrimEnd('/', '\\'), Path.GetFullPath(right).TrimEnd('/', '\\'), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static Task InsertMovieSearchAsync(ApplicationTestHost app, ApiScenes.Film film, string movieId, string release, DateTimeOffset at)
        => ApiScenes.ExecuteAsync(app, "movies",
            $"INSERT INTO movie_search_history (id, movie_id, library_id, trigger_kind, outcome, release_name, indexer_name, created_utc) VALUES ('{Guid.NewGuid():N}', '{movieId}', '{film.LibraryId}', 'manual', 'sent', '{release}', 'Fixture', '{at:O}');");

    private static Task InsertSeriesSearchAsync(ApplicationTestHost app, ApiScenes.Show show, string release, string trigger, DateTimeOffset at)
        => ApiScenes.ExecuteAsync(app, "series",
            $"INSERT INTO series_search_history (id, series_id, episode_id, library_id, trigger_kind, outcome, release_name, indexer_name, created_utc) VALUES ('{Guid.NewGuid():N}', '{show.SeriesId}', '{show.EpisodeId}', '{show.LibraryId}', '{trigger}', 'sent', '{release}', 'Fixture', '{at:O}');");
}
