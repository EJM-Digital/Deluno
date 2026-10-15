using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// Quality decisions made about real files in a real library and kept in real SQLite files: an imported file is judged on its
/// size as well as its name, a profile saved afterwards does not change that judgement, and deleting a profile that something
/// still uses leaves nothing pointing at it.
/// </summary>
public sealed class QualityDecisionsOnRealFilesTests
{
    private const long Megabyte = 1024L * 1024;

    private static string Read(ApplicationTestHost app, string database, string sql)
    {
        var path = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>().GetDatabasePath(database);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }

    private static async Task<string> ProfileAsync(ApplicationTestHost app, string mediaType, string cutoff = "WEB 480p")
    {
        var response = await app.Client.PostAsJsonAsync("/api/quality-profiles", new
        {
            name = "Cutoff " + cutoff + " " + mediaType, mediaType, cutoffQuality = cutoff, allowedQualities = "WEB 480p,WEB 720p",
            customFormatIds = string.Empty, upgradeUntilCutoff = true, upgradeUnknownItems = false
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<string> LibraryAsync(ApplicationTestHost app, string mediaType, string? profileId)
    {
        var root = Path.Combine(app.DataRoot, mediaType);
        Directory.CreateDirectory(root);
        var response = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name = mediaType, mediaType, purpose = "collection", rootPath = root, downloadsPath = (string?)null, qualityProfileId = profileId,
            importWorkflow = "copy", processorName = (string?)null, processorOutputPath = (string?)null, processorTimeoutMinutes = (int?)null,
            processorFailureMode = (string?)null, autoSearchEnabled = false, missingSearchEnabled = false, upgradeSearchEnabled = false,
            searchIntervalMinutes = (int?)null, retryDelayHours = (int?)null, maxItemsPerRun = (int?)null
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
    }

    private static async Task RunToTheEndAsync(ApplicationTestHost app, string libraryId)
    {
        using var scope = app.Services.CreateScope();
        var import = scope.ServiceProvider.GetRequiredService<IExistingLibraryImportService>();
        var started = await import.StartAsync(libraryId, CancellationToken.None);
        Assert.NotNull(started);
        for (var slice = 0; slice < 20; slice++)
        {
            var outcome = await import.RunSliceAsync(started!.Run.Id, CancellationToken.None);
            if (!outcome.MoreWorkRemains) return;
        }

        Assert.Fail("the import run did not finish");
    }

    private static string FileOfLength(string path, long length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        stream.SetLength(length);
        return path;
    }

    // ------------------------------------------------------------------ a file is judged on its size as well as its name

    [Fact]
    public async Task An_imported_film_too_small_for_its_tier_is_an_upgrade_with_the_size_in_the_reason_and_one_big_enough_is_covered()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await LibraryAsync(app, "movies", await ProfileAsync(app, "movies"));
        var small = FileOfLength(Path.Combine(app.DataRoot, "movies", "Sintel (2010)", "Sintel.2010.480p.WEB-DL.x264-GRP.mkv"), 4);
        var big = FileOfLength(Path.Combine(app.DataRoot, "movies", "Big Buck Bunny (2008)", "Big.Buck.Bunny.2008.480p.WEB-DL.x264-GRP.mkv"), 600 * Megabyte);

        await RunToTheEndAsync(app, libraryId);

        Assert.Equal("upgrade", Read(app, "movies", "SELECT wanted_status FROM movie_wanted_state WHERE file_path LIKE '%Sintel%';"));
        Assert.Contains("GB", Read(app, "movies", "SELECT wanted_reason FROM movie_wanted_state WHERE file_path LIKE '%Sintel%';"));
        Assert.Equal("0", Read(app, "movies", "SELECT quality_cutoff_met FROM movie_wanted_state WHERE file_path LIKE '%Sintel%';"));
        Assert.Equal("covered", Read(app, "movies", "SELECT wanted_status FROM movie_wanted_state WHERE file_path LIKE '%Big Buck Bunny%';"));
        Assert.Equal("1", Read(app, "movies", "SELECT quality_cutoff_met FROM movie_wanted_state WHERE file_path LIKE '%Big Buck Bunny%';"));
        Assert.Equal(4, new FileInfo(small).Length);
        Assert.Equal(600 * Megabyte, new FileInfo(big).Length);
    }

    [Fact]
    public async Task An_imported_episode_too_small_for_its_tier_does_not_meet_the_cutoff_and_one_big_enough_does_each_judged_on_its_own_file()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await LibraryAsync(app, "tv", await ProfileAsync(app, "tv"));
        var season = Path.Combine(app.DataRoot, "tv", "Northern Signal (2019)", "Season 01");
        FileOfLength(Path.Combine(season, "Northern.Signal.S01E01.480p.WEB-DL.mkv"), 4);
        FileOfLength(Path.Combine(season, "Northern.Signal.S01E02.480p.WEB-DL.mkv"), 300 * Megabyte);

        await RunToTheEndAsync(app, libraryId);

        string Met(int episode) => Read(app, "series", $"SELECT w.quality_cutoff_met FROM episode_wanted_state w JOIN episode_entries e ON e.id = w.episode_id WHERE e.episode_number = {episode};");
        Assert.Equal("0", Met(1));
        Assert.Equal("1", Met(2));
    }

    // DEFECT (read from the code, now pinned): SqliteMediaStateRepository.ReevaluateWantedStateAsync decides every title from its
    // quality name alone (it passes no file size and no tier floor), so saving the profile re-decides an undersized WEB 480p file
    // that the import marked Upgrade as Covered, because its name now meets the cutoff.
    [Fact]
    public async Task Saving_the_profile_again_leaves_an_undersized_file_marked_upgrade_as_it_was()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var profileId = await ProfileAsync(app, "movies");
        var libraryId = await LibraryAsync(app, "movies", profileId);
        FileOfLength(Path.Combine(app.DataRoot, "movies", "Sintel (2010)", "Sintel.2010.480p.WEB-DL.x264-GRP.mkv"), 4);
        await RunToTheEndAsync(app, libraryId);
        Assert.Equal("upgrade", Read(app, "movies", "SELECT wanted_status FROM movie_wanted_state WHERE file_path IS NOT NULL;"));

        var saved = await app.Client.PutAsJsonAsync($"/api/quality-profiles/{profileId}", new
        {
            name = "Cutoff WEB 480p movies", mediaType = "movies", cutoffQuality = "WEB 480p", allowedQualities = "WEB 480p,WEB 720p",
            customFormatIds = string.Empty, upgradeUntilCutoff = true, upgradeUnknownItems = false
        });

        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        Assert.Equal("upgrade", Read(app, "movies", "SELECT wanted_status FROM movie_wanted_state WHERE file_path IS NOT NULL;"));
    }

    [Fact]
    public async Task Saving_the_profile_again_re_decides_each_episode_on_its_own_file_so_the_undersized_one_stays_an_upgrade()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var profileId = await ProfileAsync(app, "tv");
        var libraryId = await LibraryAsync(app, "tv", profileId);
        var season = Path.Combine(app.DataRoot, "tv", "Northern Signal (2019)", "Season 01");
        FileOfLength(Path.Combine(season, "Northern.Signal.S01E01.480p.WEB-DL.mkv"), 4);
        FileOfLength(Path.Combine(season, "Northern.Signal.S01E02.480p.WEB-DL.mkv"), 300 * Megabyte);
        await RunToTheEndAsync(app, libraryId);

        var saved = await app.Client.PutAsJsonAsync($"/api/quality-profiles/{profileId}", new
        {
            name = "Cutoff WEB 480p tv", mediaType = "tv", cutoffQuality = "WEB 480p", allowedQualities = "WEB 480p,WEB 720p",
            customFormatIds = string.Empty, upgradeUntilCutoff = true, upgradeUnknownItems = false
        });

        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        string Met(int episode) => Read(app, "series", $"SELECT w.quality_cutoff_met FROM episode_wanted_state w JOIN episode_entries e ON e.id = w.episode_id WHERE e.episode_number = {episode};");
        Assert.Equal("0", Met(1));
        Assert.Equal("1", Met(2));
    }

    // ------------------------------------------------------------------ deleting a profile that something uses

    private static async Task<(string ProfileId, string LibraryId, string MovieId)> AProfileInUseAsync(ApplicationTestHost app)
    {
        var profileId = await ProfileAsync(app, "movies");
        var libraryId = await LibraryAsync(app, "movies", profileId);
        var movie = await app.Client.PostAsJsonAsync("/api/movies", new { title = "Sintel", releaseYear = 2010, libraryId, qualityProfileId = profileId });
        Assert.True(movie.IsSuccessStatusCode, await movie.Content.ReadAsStringAsync());
        var movieId = JsonDocument.Parse(await movie.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
        Assert.Equal(profileId, Read(app, "movies", $"SELECT quality_profile_id FROM movie_entries WHERE id = '{movieId}';"));
        Assert.Equal(profileId, Read(app, "platform", $"SELECT quality_profile_id FROM libraries WHERE id = '{libraryId}';"));
        return (profileId, libraryId, movieId);
    }

    [Fact]
    public async Task Deleting_a_profile_a_library_uses_answers_204_clears_the_librarys_choice_and_does_not_re_decide_its_titles()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (profileId, libraryId, movieId) = await AProfileInUseAsync(app);
        var wantedBefore = Read(app, "movies", $"SELECT wanted_status || '/' || wanted_reason FROM movie_wanted_state WHERE movie_id = '{movieId}';");

        var deleted = await app.Client.DeleteAsync($"/api/quality-profiles/{profileId}");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(0, long.Parse(Read(app, "platform", $"SELECT COUNT(*) FROM quality_profiles WHERE id = '{profileId}';")));
        Assert.Equal(string.Empty, Read(app, "platform", $"SELECT quality_profile_id FROM libraries WHERE id = '{libraryId}';"));
        Assert.Equal(wantedBefore, Read(app, "movies", $"SELECT wanted_status || '/' || wanted_reason FROM movie_wanted_state WHERE movie_id = '{movieId}';"));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/quality-profiles/{profileId}")).StatusCode);
    }

    // DEFECT (observed): the film's own profile choice lives in the movies database and the profile in the platform database, so
    // no foreign key can clear it: after the profile is deleted the film still names an id that no longer exists.
    [Fact]
    public async Task Deleting_a_profile_a_title_chose_leaves_the_title_naming_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (profileId, _, movieId) = await AProfileInUseAsync(app);

        var deleted = await app.Client.DeleteAsync($"/api/quality-profiles/{profileId}");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.True(string.IsNullOrEmpty(Read(app, "movies", $"SELECT quality_profile_id FROM movie_entries WHERE id = '{movieId}';")), "the film still names the deleted profile");
    }

    [Fact]
    public async Task Deleting_a_profile_says_in_Activity_what_it_moved_and_leaves_no_column_in_any_database_naming_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (profileId, _, _) = await AProfileInUseAsync(app);

        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/quality-profiles/{profileId}")).StatusCode);

        var said = Read(app, "jobs", "SELECT message FROM activity_events WHERE category = 'quality.profile.deleted';");
        Assert.Contains("1 library", said);
        Assert.Contains("1 title", said);
        foreach (var column in ProfileColumnsInEveryDatabase(app))
        {
            Assert.Equal("0", Read(app, column.Database, $"SELECT COUNT(*) FROM {column.Table} WHERE {column.Column} = '{profileId}' COLLATE NOCASE;"));
        }
    }

    [Fact]
    public async Task Every_column_that_stores_a_profile_id_in_any_database_is_one_deleting_a_profile_clears()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var found = ProfileColumnsInEveryDatabase(app).Select(item => $"{item.Database}.{item.Table}.{item.Column}").Order().ToArray();
        var cleared = Deluno.Quality.Data.SqliteQualityRepository.ProfileReferenceColumns.Select(item => $"{item.Database}.{item.Table}.{item.Column}").Order().ToArray();

        Assert.Equal(found, cleared);
    }

    private static IReadOnlyList<Deluno.Quality.Data.ProfileReferenceColumn> ProfileColumnsInEveryDatabase(ApplicationTestHost app)
    {
        var found = new List<Deluno.Quality.Data.ProfileReferenceColumn>();
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        foreach (var database in new[] { "platform", "movies", "series", "jobs", "cache" })
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = factory.GetDatabasePath(database), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            var tables = new List<string>();
            using (var list = connection.CreateCommand())
            {
                list.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
                using var reader = list.ExecuteReader();
                while (reader.Read()) tables.Add(reader.GetString(0));
            }

            foreach (var table in tables)
            {
                using var info = connection.CreateCommand();
                info.CommandText = $"SELECT name FROM pragma_table_info('{table}');";
                using var reader = info.ExecuteReader();
                while (reader.Read())
                {
                    var column = reader.GetString(0);
                    // The profile table's own key is not a reference to a profile.
                    if (table != "quality_profiles" && column.Contains("quality_profile_id", StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add(new(database, table, column, Deluno.Quality.Data.ProfileReferenceKind.Other));
                    }
                }
            }
        }

        return found;
    }
}
